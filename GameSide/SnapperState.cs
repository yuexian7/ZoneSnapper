using System;
using Colossal.Logging;
using ZoneSnapper.Engine;

namespace ZoneSnapper.GameSide
{
    /// <summary>
    /// 运行期共享状态。
    ///
    /// 为什么把设置镜像成静态 volatile 字段而不直接读属性链：
    /// Playbook 硬规则 15 ——「热路径：禁反射、禁 I/O、禁读设置属性链 ⇒ 镜像成 volatile 静态字段」。
    /// 贴合每帧都跑，读一次 ModSetting 属性链（框架实现里带本地化与序列化开销）不划算。
    /// </summary>
    public static class SnapperState
    {
        /// <summary>算法读的配置文件对象。由 Setting 在每次属性变化时整体替换（换对象而不是改字段，避免读到半套新值）。</summary>
        public static volatile ModConfig Config = ModConfig.CreateDefault();

        /// <summary>总开关的裸布尔，热路径第一道闸。</summary>
        public static volatile bool Enabled = true;

        /// <summary>
        /// 快捷键「重算贴合区域」的请求位：Setting 置 true，系统下一帧取走清零。
        /// 为什么不走系统实例引用：拿 ZoneSnapperSystem 实例要在 OnLoad 里 GetOrCreateSystemManaged，
        /// 与 updateSystem.UpdateAfter 的注册关系有重复创建风险 —— 用一个静态位标记更稳、也更符合
        /// 「热路径只读静态字段」的口径。
        /// </summary>
        public static volatile bool ResetRequested;

        /// <summary>
        /// 「上一帧区域工具正处在拖拽改形状态（State.Modify）」的交接位：主系统置起，
        /// 跟随系统在 ApplyTool 相位取走。为什么要跨系统传这一个布尔：
        /// 拖拽落定后游戏只给那块区域盖一个 <c>Updated</c> 标记，跟单列的创建（<c>Created</c>）分不开，
        /// 而我们要区分「玩家刚拖了一格 ⇒ 必须马上重描」和「路网变了但玩家没碰 ⇒ 只在开关打开时才重描」。
        /// </summary>
        public static volatile bool DragArmed;

        /// <summary>快捷键「重算贴合区域」请求跟随系统把手边这片区域按最新路网重描一遍。</summary>
        public static volatile bool FollowRequested;

        /// <summary>
        /// 拖拽改形时「玩家抓的是哪一块区域的第几格」。
        ///
        /// 【为什么要跨系统传这个】上一版这里只传一个布尔（<see cref="DragArmed"/>），
        /// 跟随系统再靠「这一帧 Area 上有 Updated 标记」去反推是哪一块 —— 而那条路根本走不通：
        ///  · ApplyTool 相位只在有提交的帧才存在（FACT：Game.Tools/ToolOutputSystem.cs:22-30），
        ///    跟随系统在那个相位里几乎拿不到帧；
        ///  · 就算拿到了，<c>Updated</c> 在帧末就被游戏自己收走（CleanUpSystem），跨帧找不到。
        /// 现在改成：主系统在 State.Modify 那一帧直接读出被拖区域的实体
        /// （FACT：AreaToolSystem.cs:3440 进 Modify 的前提就是 <c>cp.m_OriginalEntity</c> 上有 Area，
        ///  :3444-3445 把那张表快照进 m_MoveStartPositions，:2975 GetControlPoints 把它交出来），
        /// 跟随系统拿到实体就地处理，不需要任何脏标记。
        /// </summary>
        public static Unity.Entities.Entity DragTarget = Unity.Entities.Entity.Null;

        /// <summary>被拖的是第几格；-1 = 主系统只认到区域、没认到格号（此时按整圈重描）。</summary>
        public static volatile int DragTargetIndex = -1;

        /// <summary>
        /// 被拖的那一格是靠什么认出来的：①游戏的 <c>m_ElementIndex.x</c>（正路）②按位置反查最近格（兜底）。
        /// 上一版只有①，实机留下 <c>pvSkip=not-a-vertex×2253</c> ⇒ 大量拖拽我们根本没认到格号。
        /// 这两个数与 not-a-vertex 放一起看，就知道兜底够不够。
        /// </summary>
        public static long DragProbeByIndex;
        public static long DragProbeByPosition;


        /// <summary>
        /// 快捷键「退回上一次自动跟随的调整」（第五轮反馈 8）。
        /// 跟随一次会改写**存档里**的区域边界，而且是一批（改一条路可能牵动相邻十几块地），
        /// 所以玩家要求给一颗后悔键。实现是单级快照：跟随系统每次改写前把原来那一圈完整存下，
        /// 按下这一颗就把那一批全部还原（详见 ZoneSnapperFollowSystem 的 Undo 段）。
        /// </summary>
        public static volatile bool UndoFollowRequested;

        /// <summary>跟随/改形重描的计数：写了多少块、形状没变跳过多少块、自检拦下多少块。实机判就读这三个数。</summary>
        public static long FollowedAreas;
        public static long FollowSkippedSame;
        public static long FollowRejected;

        /// <summary>退回了多少块区域的跟随改动（快捷键按一次涨一批）。</summary>
        public static long FollowUndoneAreas;

        /// <summary>撤销栈里现在存着几块区域（玩家看得到「还能退回几步」就靠这一格）。</summary>
        public static volatile int UndoDepth;

        /// <summary>
        /// 「本模组这一局坏掉了」的总闸：一旦置起，两个系统第一行就返回，绝不碰游戏的任何数据。
        /// 为什么必须有它：v0.1.0 实机里 OnLoad 中途抛异常（DynamicBuffer&lt;Game.Areas.Node&gt; 对模组
        /// 程序集不注册）⇒ Harmony 一步没走到 ⇒ 主系统带着空的手动栈照跑，每帧把玩家的第一个节点抹掉。
        /// **半死不活比完全不工作有害得多** —— 玩家遇到的是「游戏自己的区域工具坏了」。
        /// 它不由玩家的开关清掉：只有重启游戏（重新走 OnLoad）才会复位。
        /// </summary>
        public static volatile bool Broken = false;

        /// <summary>打上的补丁数（0/1/2）。日志与统计行都要带它：「装了但没生效」必须一眼可判。</summary>
        public static volatile int HooksInstalled = 0;

        /// <summary>「想写但被 MayWrite 挡下」的次数。稳态下永远是 0；一旦增长说明事件漏收或别人动了表。</summary>
        public static long Desyncs;

        /// <summary>
        /// 「减点写盘」成功的次数 —— 需求 3 的正证：右键弹掉一个手动节点时，那条边的描边点必须跟着一起走，
        /// 这必然要往表里少写几格。正常应当与 <see cref="Pops"/> 同量级；远大于它说明我们在乱减点。
        /// </summary>
        public static long ShrinkWrites;

        /// <summary>接管进来的控制点数（编辑/重建已有区域时走这条路）。填了 recreate 闸被放开的债。</summary>
        public static long Adopted;

        /// <summary>
        /// 收到左键事件但**游戏那一击其实没落点**的次数（距离小于游戏自己的最小节点间距，
        /// 见 ProjectKit.GameAcceptedCommit）。这个数字增长是正常的：玩家点得密而已。
        /// 它要是跟 Commits 一个量级，说明我们的间距基线比游戏放宽太多。
        /// </summary>
        public static long RejectedCommits;

        /// <summary>本局生效统计，全部走日志当机器凭据（Playbook 步骤 5：每版至少留一处机器可判的凭据）。</summary>
        public static long Commits;
        public static long Pops;
        public static long SnapHits;
        public static long SnapMisses;
        public static long TraceHits;
        public static long TraceFallbacks;
        /// <summary>
        /// 其中有多少条是**节点续接**（第五轮反馈 7）：两端之间没有连续路径，只贴出了靠路上的那一段。
        /// 这个数单独记一间是因为第五轮那份账里 <c>fb=NotOnNetwork×501</c> 占了回退的绝大多数，
        /// 而续接上线后「NotOnNetwork 少了多少」必须能区分两种原因：
        ///  · 少了、且 cont 在涨 ⇒ 续接把它们转成了部分贴合（想要的结果）；
        ///  · 没少、cont 也不涨 ⇒ 那些边的两端本来就都不在路上（该走手动路径，不是缺陷）；
        ///  · 没少、cont 在涨 ⇒ 续接在跑但没被算进回退统计，是账目的错，不是引擎的错。
        /// </summary>
        public static long Continuations;

        /// <summary>
        /// 走线自己长出来的**接入点**总数（第八轮反馈 2/3/6/8/10 的接入点模型）。
        /// 这一间与 <see cref="Continuations"/> 是此消彼长的那一对：
        ///  · attach 在涨而 cont 不涨 ⇒ 能连通的两端走的是正常描边（想要的结果）；
        ///  · attach 一直是 0 而玩家又说「不贴合」⇒ 要么半径真的小于一切，要么 Attach 被别的闸拦了；
        ///  · cont 还在大涨 ⇒ 端点附近确实没有本档该贴的东西（该看一眼实机走廊是不是抓窄了）。
        /// </summary>
        public static long AttachedPoints;

        /// <summary>
        /// 被拆掉的**尖刺接缝**数（第八轮反馈 3/10 与 6/8 撞车的位置：玩家那格不吸附、
        /// 左右两条走线却在同一个坐标各接进网络一次 ⇒ 零宽度的裂缝，游戏能存但三角化不出东西）。
        /// 这个数一直在涨说明两条规则确实在抢同一格；不涨则说明那种形状本来就没出现。
        /// </summary>
        public static long DespikedJoints;

        /// <summary>
        /// 采样阶段就被请出快照的**地下物体**数（第八轮反馈 3：隧道/下沉路/地下的建筑
        /// 一律不是吸附与描边目标）。放在采样侧是因为那里才知道游戏数据长什么样，
        /// 而引擎层的排除（PolicyKit.IsTargetNet / ObjectAllowed）是第二道闸。
        /// </summary>
        public static long UndergroundDropped;

        /// <summary>
        /// 交付顶点被从**路口成员节点**挪到那个路口中心点的格数（第八轮反馈 5，
        /// 统计行的 <c>junc=</c>；来源是 <c>TraceResult.Clamped</c>，即 <c>TraceKit.ClampToJunctions</c>）。
        /// 这一间是「路口那一格落在哪个坐标」的唯一机器凭据：
        ///  · 画过真路口时 junc 在涨 ⇒ 归并吃到了数据，边界确实只拐在中心那一个点；
        ///  · 一直是 0 而玩家说「还是在偏心的那一格拐弯」⇒ <c>JunctionKit.MERGE_SPAN</c>（15 米）
        ///    在真实人行道路口上太窄，回去调常数；
        ///  · 涨得比 attach 还快 ⇒ 疑似把长边中间的采样点也并进去了（那是判据写错，不是路口多）。
        /// 跟随/拖拽那两条链路不重复计数（它们走的也是同一段引擎代码，但结果不经过这条累加），
        /// 所以这个数是**绘制期**的下界，不是总次数。
        /// </summary>
        public static long JunctionClamps;

        public static long AutoNodesWritten;
        public static long RetraceRuns;

        /// <summary>
        /// 有多少个自动点是**照抄邻接区域节点的坐标**（第六轮反馈 7，TraceKit.AdoptBorderNodes 交出来的数）。
        /// 实机验收「两块地共用一段路时节点有没有取齐」就看它是不是在涨：
        /// 涨了 = 共线段逐位相同（游戏认得出是同一条边，不会判成物体碰撞）；
        /// 一直是 0 而玩家又确实挨着已有区域画 = 认亲的容差/平行判据在实机数据上不成立，得回去调。
        /// </summary>
        public static long AdoptedBorderNodes;
        /// <summary>
        /// 「锚点下标已经不属于当前快照」的次数 —— 第三轮实机那个「snap 很高但 traceHit 只有 17」的病灶，
        /// 修好后这个数字应长期停在 0（>0 就说明重抓快照与手动栈之间还有没被 <c>AnchorRebind</c> 覆盖的路径）。
        /// </summary>
        public static long StaleIndexBlocks;
        public static double LastBuildMillis;

        // ———— 闭合边待补写队列（主系统 → 提交后系统，同一帧内交接，全程主线程）————
        private static System.Collections.Generic.List<Engine.P3> s_pendingRing;
        private static int s_pendingSlots;
        private static long s_pendingStackSig;
        private static int s_pendingFrame;

        /// <summary>登记时的帧号。第六轮用它给闭合环一个"认领窗口"（见 PendingRingAge 的注释）。</summary>
        public static int PendingRingAge()
        {
            if (s_pendingRing == null) return int.MaxValue;
            return UnityEngine.Time.frameCount - s_pendingFrame;
        }

        /// <summary>本次绘制投影出的槽位数（含游标）。提交后系统用它做逐点比对。</summary>
        public static int PendingSlotCount { get { return s_pendingSlots; } }

        /// <summary>已补写过闭合边的区域数（实机验收要看这个数字）。</summary>
        public static long RingsAdopted;

        /// <summary>这个栈的闭合环是不是已经算过了（避免每帧重跑一次图搜索）。</summary>
        public static bool HasPendingRingFor(long stackSig)
        {
            return s_pendingRing != null && s_pendingStackSig == stackSig;
        }

        /// <summary>登记一条待补写的闭合环。同一个手动栈只登记一次，避免每帧重跑一次图搜索。</summary>
        public static void SetPendingRing(System.Collections.Generic.List<Engine.P3> ring, int slotCount, long stackSig)
        {
            if (s_pendingRing != null && s_pendingStackSig == stackSig) return;
            s_pendingRing = ring;
            s_pendingSlots = slotCount;
            s_pendingStackSig = stackSig;
            s_pendingFrame = UnityEngine.Time.frameCount;
        }

        /// <summary>取用但不清空：区域实体未必与闭合点击同帧出现，要允许后面几帧继续认领。</summary>
        public static System.Collections.Generic.List<Engine.P3> TakePendingRing()
        {
            return s_pendingRing;
        }

        /// <summary>用完或换会话时丢弃。</summary>
        public static void DiscardPendingRing()
        {
            s_pendingRing = null;
            s_pendingSlots = 0;
            s_pendingStackSig = 0;
        }

        /// <summary>当前贴合来源（SnapKind 名），供 HUD/日志与实机验收判读。</summary>
        public static volatile string LastSnapKind = "Free";

        public static void ResetCounters()
        {
            Commits = 0;
            Pops = 0;
            SnapHits = 0;
            SnapMisses = 0;
            TraceHits = 0;
            TraceFallbacks = 0;
            AutoNodesWritten = 0;
            RetraceRuns = 0;
            StaleIndexBlocks = 0;
            AdoptedBorderNodes = 0;
        }
    }

    /// <summary>
    /// 统一日志口。注意 Colossal 的 ILog 方法名是 <c>Warn</c> 而不是 Warning（Playbook 步骤 1 记过的坑）。
    /// 日志路径由游戏按 logger 名落到 Logs\ZoneSnapper.log（Playbook 步骤 5：每个 logger 各自一份文件）。
    /// </summary>
    public static class SnapperLog
    {
        public static ILog Log = LogManager.GetLogger("ZoneSnapper").SetShowsErrorsInUI(false);

        public static void Info(string msg)
        {
            try { Log.Info(msg); } catch (Exception) { }
        }

        public static void Warn(string msg)
        {
            try { Log.Warn(msg); } catch (Exception) { }
        }

        /// <summary>
        /// 错误一律「只记不抛」：贴合是增强功能，任何内部异常都不能让玩家画不出区域。
        /// 这条纪律来自 Playbook §3.5「热路径必须 try/catch 兜底放行原文」。
        /// </summary>
        public static void Error(string msg)
        {
            try { Log.Error(msg); } catch (Exception) { }
        }
    }
}
