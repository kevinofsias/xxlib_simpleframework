﻿#pragma warning disable 0169, 0414
using TemplateLibrary;

[CategoryNamespaces]
enum ServiceTypes
{
    Loader, Discovery, Navgate, Proxy, Login, Lobby, Room, DB, DBCommit
}

class Service
{
    ServiceTypes type;
    ushort instanceId;
    string ip;
    int port;
}

namespace Discovery
{
    [Desc("服务启动后用该指令告知 Discovery")]
    class Register
    {
        [Desc("实例id")]
        ushort instanceId;

        [Desc("端口号")]
        int port;

        //ulong versionNumber;
    }

    [Desc("注册结果--成功")]
    class RegisterResult_Success
    {
        [Desc("现有服务实例列表")]
        List<Service> services;
    }

    [Desc("注册结果--失败: 重复的实例id")]
    class RegisterResult_DupeInstanceId
    {
    }

    // more errors
}

namespace Proxy
{
    [Desc("附在待转发的包前面, 表明包是 谁 发过来的")]
    class UserToken
    {
        ulong token;
    }
}



/* 

    基本需求

    稳定, 安全, 易扩展, 易管理, 运营成本不高, 能支撑百万人同时在线



    基础结构( 不含 游戏, 登录 / 排队, 活动, 比赛, 商城, 仓储, 聊天, 邮件, 查询, 排行, 日志, BOT, 后台...... )

    Manage, Loader, Discovery, Navgate, Proxy, DB, DBCommit



    物理分布

    Manage 是服务器管理后台( 1 份 ), 借助 Loader, 可针对每台机器 进行 service 开关配置

    Loader 每台机器开机自启动运行一份, 连接到 Manage
    其他服务 受 Loader 控制, 根据 Manage 的配置, 于相应服务器动态开启

    Loader 同时也监视 具体 service 实例的运行健康状态

    Discovery 为发现服务, 接受各 service 注册并拉已注册 service 清单( 含实例type, id, ip, port 之类 )
    这样子 service 就可以在接下来 根据清单来查找自己感兴趣的 service, 并与之建立连接.
    连接过程是双向的. 不管谁依赖谁, 都是后启动的去找先启动的 service 建立连接.

    Discovery 服务需要先于其他 service 启动( Manage, Loader 除外 )

    Navgate 为导航服务, 其结构本身可能是一个金字塔形, 有可能在多台机上开多份, 但共享相同数据.
    导航结果决定了 client 会去连哪个 或哪些 proxy

    Proxy 为数据代理, 具有 数据转发 / 汇聚 / 协议转换 / 限速 / QOS / 基础安全校验 / 分流抗攻击 / 确保物理连接不断 等功用

    DB 为数据中心( 1 份 ), 内存缓存热数据, 向内网提供各种 高效的 无阻塞的 逻辑单点的 数据操作.

    DBCommit 为数据同步提交服务( 1 份 ), DB 的所有内存变化, 均及时生成相应的 数据库操作指令, 并放入 Redis 蓄水池
    这个服务从蓄水池不断的取出指令, 逐个执行, 以实现高速及时的修改内存 但也能慢慢的将数据库同步的效果.

    DB / DBCommit 需要不怕死, 不怕重启, 能动态恢复数据. 这需要 Redis 那边做一定的已执行指令备份设计, 
    以及 DB 本身的操作 需要事务记录诸如 "流水号" 之类的东西, 以便异常到来时, 可定位到相应的 数据库操作指令, 继续执行



    网络连接对应关系

    Manage 与 Loader 一对多
    Discovery 与 services 一对多
    Navgate 与 Proxy 一对多
    Proxy 与 services 多对多



    通信流程与需求
 
    client 先从 navgate( 可能有多个, udp, 首包即含复杂校验信息 ) 得到要连接哪个 proxy 的信息, 然后去就去连指定 proxy
    
    client 发送相应指令到 proxy. proxy 负责转发到相应 service, 并转发处理结果( 应答 / 推送均可 )
    对于断线重连的情况, proxy 应该告知 client, 连接到原有 proxy 上, 并与 context 重连.
    proxy 上面理论上讲有一段 待发送 & 已发送的 数据包队列, 其生命周期手工配置, 用于断线重连补发
    针对补发的需求, 向 client 发送的包, 汇聚到一个队列, 有 "包编号", 以便于在队列中定位

    proxy 同时也应该有 上 & 下行 限速, 带宽均分功能, 用以保护有限的总带宽尽量提升每机玩家在线数
    对于可并行下发的流量( 例如 视频流 和 游戏指令 ), 理论上讲需要做分 channel 优先级限速( 切片组合, 先确保关键性数据 channel 的包占据流量大头 )

    proxy 同时也应该提供优化后的 udp 通信能力( udp with kcp 之类 ), 以显著降低网络延迟

    proxy 同时也应该有 多级转发, 多点接入, 动态切换, 以分摊 / 转移流量, 做到防 ddos 的效果

    proxy context 中, 针对每类指令, 应建立 client 到 service 的双向映射, 以便于双向转发.
    当前以 生成器 扫描根命名空间 的方式来区分 service 类型, 公共结构都不加命名空间.
    生成器 需要生成与上述规则对应的 根命名空间枚举 以及 ServiceTypeId< 包 >.value = 根命名空间下标 设置代码
    考虑令某特殊的 Enum 名字用来枚举这些 根命名空间, 以便命名空间可以任意使用
    
    对于一种类型有多个 service 的情况, 存在一个 Redirect 的设计, 即首次转发都是发给同类型 service 的 "交换机"
    client context, 存在 "某类型 service 发送目标" 的映射. 如果首次发送, 该映射并未建立. 遂先向 "交换机" 发送导航请求
    "交换机" 根据某种理由( 负载均衡? 断线重连? 目标服务已死? ), 向 proxy 回复 redirect
    proxy 在收到 redirect 之后, 将该类型 service 在 context 中 建立映射, 并继续发包.
    这可能导致数据包滞留. 但也要等到映射建立之后才发送后续.

    client 发向 proxy 的包, 可以不带任何地址信息. proxy 通过查询 context 来定位到目标 service 的连接并转发
    转发时需要附上该 client 的唯一标识, 以便 service 发送数据过来的时候, 定位到 client 的连接并转发
    service 在收到包之后, 也需要对 "从哪个 proxy 收到的 client xxx 的数据" 做相应的映射
    这个映射可能会发生改变( 当 service 中的 context 与 proxy 中的 context 生命周期不一致时 ), 后续都是往当前 proxy 发送
    
    也存在一种 proxy 死掉的可能性( 代码不稳定? 虚拟主机死机 掉线啥的 ), 考虑采取先暂存一段时间, 待目标确认接收成功后 再移除


    
    开局流程

    玩家位于大厅时, 如果通过匹配, 定位到了目标战斗 service, 则此时需要将玩家 "引" 过去. 这里 proxy 并不针对此类 service 做 "映射",
    而是大厅告知 client 它需要连接的 service 的 address 信息, 在 client 发到 proxy 的包( 用于与目标服务器通信 )中, 需要携带该 address 信息.
    同时, 大厅也告知目标 service, 要连进来的 client 的 唯一标识. ( 这个过程有可能先于 大厅告知 client 发生, 此举可以顺便探测目标 service 的健康 )

    该做法相比同类型映射方案, 可以更灵活的应对需要 "同时进行", "多开" 的需求. 
    但相应的, 也有暴露 服务端 逻辑流程的风险, 恶意 client 可能直接伪造目标地址以实施某些因 开发过程中的疏忽造成的漏洞攻击
    故 此种做法, 只针对部分需要并行的同类型服务运用




    带宽需求分析

    以棋牌为例, 此类游戏交互量较小. 平均每用户每秒流量可能不足 1k ( 除开流媒体相关 ). 甚至部分游戏类型间隔几秒才操作一次, 平时基本没啥流量

    如果设计层面存在 "进出大厅, 能看到桌子上坐有人" 的概念, 则玩家进出大厅的数据, 需要及时同步,
    刚进入大厅时的流量 可能略大, 这和大厅内有多少玩家, 以及每玩家的数据的详尽程度有关. 

    如果设计层面存在 "玩家形象展示" 的概念, 则玩家除开本身的 id, 金币, 等级?? 成就?? 这些少量数据以外, 还会附带纸娃娃数据表
    如此估算下来, 恶劣情况下, 每玩家数据可能达到 1k 一位. 设 每"大厅" 平均 200 名玩家, 则每次进出大厅, 可能产生 200k 瞬间流量.
    如果遇到恶意客户端, 在服务器没有防备的情况下, 模拟频繁进出人多的房间, 则极易耗光带宽
    
    配合上文中提到的 proxy 的职能, 如果做限速, 针对这种大厅数据, 又会造成客户端体验受到影响( 比如进出大厅加载时间显著延长 )
    这属于不可调和之矛盾, 理论上讲可通过与策划案配合, 以避免出现此类情况. 
    比如限制每 "大厅" 的同时在线数, 允许分时延迟加载纸娃娃数据, 客户端做本地玩家数据缓存( 带版本号, 过期时间, 按需批量 / 个体加载 )
    控制得好的情况下, 进出大厅时, 只收发每玩家的基础数据( id, 版本号 ), 200 * 12 = 2.4k, 完全可以接受
    之后, 客户端根据本地缓存, 分时向 查询服务 拉取玩家具体的展示数据, 1秒拉 2 人? 这样也就能将流量控制在 2k 左右, 也可以接受
    多数情况下, 客户端会根据显示区域, 优先拉取部分急需显示的玩家数据, 或是已经进入游戏开打, 此时便不再继续产生流量.

    基于上文提到的多 proxy 特性, 理论上讲玩家也可以同时建立对多个 proxy 的连接, 数据可从不同的 proxy 下发, 进一步降低费用
    具体的, 将 proxy 按带宽特性分组, 大体分为 "固定带宽" 与 "峰值 / 按流量计费" 两种.
    几秒一次的稳定 & 少量的游戏内通信, 可归并到 "固定带宽", 而进出大厅 拉排名 之类的行为, 可归并到 "峰值 / 按流量计费"
    这样一方面大流量数据不需要限速太多( 还是要限 ), 有助于改善玩家体验, 另一方面也能节省网络费用
    



    硬件平台, 指标 与开发分析

    考虑到省钱, 高效, 上述核心服务应该使用 C++ 语言开发, 并运行于 linux 服务器平台. 非核心服务, 是否 C++ 开发, 并不关注

    // todo: 进一步阐述每种服务的典形压力与 cpu 内存 网络 磁盘 占比




    工期 / 时间 / 里程杯 / 人员需求

    // todo: 这个要等具体的案子

*/






//enum Color
//{
//    Red, Blue
//}

//struct Pos
//{
//    float x, y;
//}

//class SceneObj
//{
//    [Desc("所在场景")]
//    Scene scene;
//}

//class Monster : SceneObj
//{
//    [Desc("位于 scene monster 容器中的下标 for 交换快删")]
//    int scene_monsters_index;
//    [CreateInstance]
//    string name;
//    Color color = Color.Blue;
//    Pos pos;
//}

//class Deamon : Monster
//{
//}

//class Butcher : Monster
//{
//}

//class Scene
//{
//    [CreateInstance]
//    List<Monster> monsters;
//}
