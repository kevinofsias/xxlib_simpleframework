﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using TemplateLibrary;

public static class GenCPP_SQLite
{
    public static void Gen(Assembly asm, string outDir, string templateName)
    {
        var sb = new StringBuilder();
        var ts = asm._GetTypes();

        // usings
        sb.Append(@"#pragma once
#include """ + templateName + @"_class.h""
namespace DB
{
");

        // 扫带有 [SQLite] 标志的 interface , 生成相应的函数
        var ifaces = ts._GetInterfaces<SQLite>();
        foreach (var iface in ifaces)
        {
            // namespace xxx {
            if (iface.Namespace != null)
            {
                sb.Append(@"
namespace " + iface.Namespace + @"
{");
            }

            // struct {
            sb.Append(iface._GetDesc_Cpp(4) + @"
    struct " + iface.Name + @"
    {
		xx::SQLite* sqlite;
		xx::MemPool& mp;
		xx::SQLiteString_v s;
		bool hasError = false;
		int const& lastErrorCode() { return sqlite->lastErrorCode; }
		const char* const& lastErrorMessage() { return sqlite->lastErrorMessage; }

        //int recordsAffected // todo

		" + iface.Name + @"(xx::SQLite* sqlite) : sqlite(sqlite), mp(sqlite->mempool()), s(mp) {}

");

            // todo: 如果参数都是简单数据类型( 不含 List / Sql拼接段 啥的 ), 生成 SqlCommand + Parameter 复用

            // members = default vals;
            var fs = iface._GetMethods();
            foreach (var f in fs)
            {
                var fn = f.Name;
                if (!f._Has<Sql>())
                    throw new Exception("no [Sql] attribute on " + iface.Name + "." + fn);

                var ps = f.GetParameters();
                var rt = f.ReturnType;
                var rtn = rt._GetSafeTypeDecl_Cpp(templateName);

                sb.Append(@"
        xx::SQLiteQuery_p query_" + fn + @";

" + f._GetDesc_Cpp(8) + @"
        " + rtn + " " + fn);

                if (ps.Length > 0)
                {
                    sb.Append(@"
        ");
                }

                sb.Append(@"(");
                foreach (var p in ps)
                {
                    if (p != ps[0])
                    {
                        sb.Append(",");
                    }
                    var pt = p.ParameterType;

                    sb.Append(p._GetDesc_Cpp(12) + @"
            " + pt._GetSafeTypeDecl_Cpp(templateName) + " const& " + p.Name);

                    if (p.HasDefaultValue)
                    {
                        sb.Append(" = " + p._GetDefaultValueDecl_Cpp(templateName));    // todo: string 支持
                    }
                }

                if (ps.Length > 0)
                {
                    sb.Append(@"
        ");
                }
                sb.Append(@")
        {
			hasError = true;
			auto& q = query_" + fn + @";
            // recordsAffecteds.Clear();
");

                var sqls = f._GetSql()._SpliteSql();

                // 看看有没有 List
                bool hasList = false;

                // 合并 sql 成 1 个串 for NewQuery
                var sqlcombine1 = new StringBuilder();
                foreach (var o in sqls)
                {
                    if (o is string)
                    {
                        sqlcombine1.Append(o);
                    }
                    else if (ps[(int)o].ParameterType._IsList())
                    {
                        hasList = true;
                    }
                    else
                    {
                        sqlcombine1.Append("?");
                    }
                }

                // 将 sqls 中参数是 List 类型的转成 "?" string
                var sqls2 = sqls.Select((sql) =>
                {
                    if (sql is string || ps[(int)sql].ParameterType._IsList()) return sql;
                    return "?";
                });

                // 合并连续出现的 string
                var sqlcombine2 = new StringBuilder();
                var sqls3 = new System.Collections.Generic.List<object>();
                foreach (var sql in sqls2)
                {
                    if (sql is string)
                    {
                        sqlcombine2.Append(sql);
                    }
                    else
                    {
                        sqlcombine2.Append("?");
                    }
                }
                if (sqlcombine2.Length > 0) sqls3.Add(sqlcombine2.ToString());
                sqls = sqls3;



                if (hasList)
                {
                    sb.Append(@"
            s->Clear();");
                    foreach (var o in sqls)
                    {
                        if (o is string)
                        {
                            sb.Append(@"
            s->Append(R""=-=(" + o + @")=-="");");
                        }
                        else
                        {
                            var p = ps[(int)o];
                            var pn = p.Name;
                            var pt = p.ParameterType;
                            sb.Append(@"
            s->SQLAppend(" + pn + @");");
                        }
                    }
                    sb.Append(@"
            q = sqlite->CreateQuery(s->C_str(), s->dataLen);");
                }
                else
                {
                    sb.Append(@"
			if (!q)
			{
				q = sqlite->CreateQuery(R""=-=(" + sqlcombine1 + @")=-="");
			}");
                }
                if (rtn == "void")
                {
                    sb.Append(@"
			if (!q) return;");
                }
                else
                {
                    sb.Append(@"
            " + rtn + @" rtv;
			if (!q) return rtv;
            rtv.Create(mp);");
                }

                var ps2 = ps.Where(p => !p.ParameterType._IsList());
                if (ps2.Count() > 0)
                {
                    sb.Append(@"
            if (q->SetParameters(");
                    foreach (var p in ps2)
                    {
                        sb.Append(p.Name + ", ");
                    }
                    sb.Length -= 2;
                    sb.Append(@")) return" + (rtn == "void" ? "" : " rtv") + @";
");
                }

                // 根据函数返回值类型，生成不同的代码段。

                // 多结果集 SQLite 不支持
                if (rt._IsTuple())
                {
                    throw new NotSupportedException();
                }
                // 单结果集
                else if (rt.IsGenericType && rt.Name == "List`1")
                {
                    var ct = rt.GenericTypeArguments[0];
                    var ctn = ct._GetTypeDecl_Cpp(templateName);

                    // 用户类
                    if (ct._IsUserClass())
                    {
                        sb.Append(@"
			if (!q->Execute([&](xx::SQLiteReader& sr)
            {
				auto& r = rtv->EmplaceMP();");
                        var ctfs = ct._GetFields<Column>();
                        if (ctfs.Count() == 0)
                            throw new Exception("the class's fields can't be empty");

                        for (int i = 0; i < ctfs.Count; ++i)
                        {
                            var m = ctfs[i];
                            var mt = m.FieldType;
                            var mn = m.Name;
                            var getfn = mt._GetDataReaderFuncName_Cpp(i);

                            sb.Append(@"
                " + (mt._IsSqlNullable() ? "if (!sr.IsDBNull(0)) " : "") + "rtv->" + mn + @" = " + getfn + @";");
                        }
                    }
                    // 简单类型
                    else
                    {
                        sb.Append(@"
            if (!q->Execute([&](xx::SQLiteReader& sr)
            {
				" + (ct._IsSqlNullable() ? @"if (sr.IsDBNull(0)) rtv->Emplace();
                else " : "") + @"rtv.Add(" + ct._GetDataReaderFuncName_Cpp(0) + @");");
                    }
                    sb.Append(@"
            }))
			{
				rtv = nullptr;
				return rtv;
			}
			hasError = false;
			return rtv;");
                }
                // 单行 / 单个结果
                else
                {
                    if (rt._IsVoid())
                    {
                        // 无返回值
                        sb.Append(@"
            if (!q->Execute()) return;");
                    }
                    // 用户类
                    else if (rt._IsUserClass())
                    {
                        var rtfs = rt._GetFields<Column>();
                        if (rtfs.Count() == 0)
                            throw new Exception("the class's fields can't be empty");

                        sb.Append(@"
			if (!q->Execute([&](xx::SQLiteReader& sr)
            {");
                        for (int i = 0; i < rtfs.Count; ++i)
                        {
                            var m = rtfs[i];
                            var mt = m.FieldType;
                            var mn = m.Name;
                            var getfn = mt._GetDataReaderFuncName_Cpp(i);

                            sb.Append(@"
                " + (mt._IsSqlNullable() ? "if (!sr.IsDBNull(0)) " : "") + "rtv->" + mn + @" = " + getfn + @";");
                        }
                        sb.Append(@"
            })) return rtv;
			hasError = false;
			return rtv;");
                    }
                    // 简单类型
                    else
                    {
                        sb.Append(@"
            if (!q->Execute([&](xx::SQLiteReader& sr)
            {");
                        sb.Append(@"
                " + (rt._IsSqlNullable() ? "if (!sr.IsDBNull(0)) " : "") + "rtv = " + rt._GetDataReaderFuncName_Cpp(0) + @";");
                        sb.Append(@"
            })) return rtv;
			hasError = false;
			return rtv;");
                    }
                }

                // func }
                sb.Append(@"
        }");
            }


            // class }
            sb.Append(@"
    };");

            // namespace xxx }
            if (iface.Namespace != null)
            {
                sb.Append(@"
}");
            }
        }

        // template namespace /
        sb.Append(@"
}
");

        sb._WriteToFile(Path.Combine(outDir, templateName + "_sqlite.h"));
    }
}