/*

MP 定义中须含有下面这些类型

typedef xx::MemPool<
.............
SceneBase,
SceneObjBase,
FSMBase,
xx::List<FSMBase*>, 
xx::List<SceneObjBase*, true> 
................> MP;

*/

// 带 Update 虚函数的类的基类
struct UpdateBase : xx::MPObject
{
	// 返回非 0 表示自杀. 正数通常是正常结束生命, 负数通常表示出错
	virtual int Update() = 0;
};

struct SceneBase;
struct SceneObjBase;

// 状态机基类. 只能用 SceneObjBase 的函数来创建. 不可以直接 Release. 自杀是用 PopFSM 或 Update 返回非 0
struct FSMBase : UpdateBase
{
	SceneObjBase* owner;
	FSMBase(SceneObjBase* owner) : owner(owner) {}
};

// 场景的子的基类( 场景对象 ). 只能用 SceneBase 的函数来创建.
struct SceneObjBase : UpdateBase
{
	// 指向场景容器
	SceneBase* sceneBase;

	// 位于场景怪容器中的下标( for 交换式快删 )
	uint32_t sceneObjsIndex;

	// todo: 提供一个容器用来存上个 FSM 的执行结果( 正常结束, 满足退出条件, 打断事件来临之类 )
	xx::List<FSMBase*>* fsmStack;
	FSMBase* currFSM = nullptr;
	FSMBase* deadFSM = nullptr;		// 延迟杀掉

	SceneObjBase(SceneBase* sceneBase);
	~SceneObjBase();

	template<typename T, typename ...Args>
	T* CreateFSM(Args&&...args);
	void SetFSM(FSMBase* fsm);
	void PushFSM(FSMBase* fsm);
	void PopFSM();
	virtual int Update() override;
};

// 场景基类( 整合了内存池, 对象容器, LUA State )
struct SceneBase : xx::MPObject
{
};
