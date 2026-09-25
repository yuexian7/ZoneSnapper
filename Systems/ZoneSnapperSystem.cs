using System;
using System.Collections.Generic;
using System.Diagnostics;
using Colossal.Logging;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Scripting;
using ZoneSnapper.Engine;
using ZoneSnapper.GameSide;
using ZoneSnapper.Patches;

namespace ZoneSnapper.Systems
{
    /// <summary>
    /// Zone Snapper 主系统：跑在 AreaToolSystem 之后、同一个 ToolUpdate 相位，每帧把「玩家的手动节点栈」
    /// 投影成游戏控制点表（[m0, 描边…, m1, …, mN, 光标]）。
    ///
    /// 【机制链（全部有反编译凭据）】
    ///  1. AreaToolSystem 是唯一「手动放节点→闭合」的区域绘制工具，覆盖 District/Lot/Surface/Space/MapTile
    ///     （FACT：ATS:29 类声明、ATS:2557 m_ControlPoints、ATS:2973 公开读写口）。
    ///  2. 它的表最后一格恒为跟随鼠标的游标（FACT：ATS:3678 math.select(0, Length-1, state==Create)），
    ///     提交时校验倒数第二格到游标的距离（FACT：ATS:3509-3511）。⇒ 描边点只能插在每个手动节点之前，
    ///     本文件里 <see cref="ProjectKit"/> 的投影形状就是为了守住这两条。
    ///  3. 左键 = Apply、右键 = Cancel，都是 private（FACT：ATS:3151-3157 分派，Apply ATS:3408，Cancel ATS:3283）
    ///     ⇒ 只用 Harmony 拿这两个事件（见 AreaToolPatches），其余全部走数据层。
    ///  4. 道路/铁路变了会打 Game.Common.Updated，Game.Net.UpdateCollectSystem 公开 netsUpdated 与脏矩形
    ///     （FACT：UpdateCollectSystem.cs:328/330/468）⇒ 需求 5 的自动跟随是事件驱动，不做定时全量重刷。
    ///
    /// 【为什么整体 try/catch】Playbook §3.5：增强类功能内部异常绝不能让玩家画不出区域。
    /// 兜底方式是「这一帧什么都不做」，游戏的工具照样按原逻辑工作。
    /// </summary>
    [Preserve]
    public partial class ZoneSnapperSystem : GameSystemBase
    {
        private AreaToolSystem m_areaTool;
        private ToolRaycastSystem m_toolRaycast;
        private PrefabSystem m_prefabSystem;
        private UpdateCollectSystem m_netCollect;
        private WorldSampler m_sampler;
        private WorldSampler.SamplerLookups m_lk;

        private readonly ManualStack m_stack = new ManualStack();
        private PlacedNode m_lastCursorNode;
        private Projection m_lastProjection;

        private WorldSnapshot m_world;
        private Box2 m_worldBox = Box2.Empty();
        /// <summary>本次绘制已经覆盖过的「所有角 + 当前两端」的并集框（不含外扩）——走廊单调扩张靠它。</summary>
        private Box2 m_strokeRaw = Box2.Empty();
        /// <summary>复用的锚点位置表（避免每次重抓都 new 一个 List）。</summary>
        private readonly System.Collections.Generic.List<Engine.P3> m_anchorPts =
            new System.Collections.Generic.List<Engine.P3>();
        private bool m_warnedClamp;
        private long m_worldSig;

        // —— 拖拽预览专用的一份会话状态（与绘制中的 m_world/m_strokeRaw 完全分开，理由见 EnsureDragWorld）
        private WorldSnapshot m_dragWorld;
        private Box2 m_dragBox = Box2.Empty();
        private Box2 m_dragRaw = Box2.Empty();
        private readonly System.Collections.Generic.List<Engine.P3> m_dragRing =
            new System.Collections.Generic.List<Engine.P3>();
        private readonly System.Collections.Generic.List<Engine.P3> m_dragAnchors =
            new System.Collections.Generic.List<Engine.P3>(2);
        /// <summary>预览异常只说一次，不刷屏（Playbook：日志刷屏会把下一次事故的证据淹掉）。</summary>
        private bool m_previewLogged;

        private int m_staleLogTick;
        private Stopwatch m_watch;

        /// <summary>本帧描边重算条数上限：大城市一次改路会脏掉很多边，不能一帧全算。</summary>
        private const int MAX_RETRACE_PER_FRAME = 2;

        /// <summary>
        /// 被 MayWrite 连续挡下多少帧后触发自愈（约 0.25 秒）。
        /// 太短会在「游戏自己那一帧还没落定」的过渡帧上误收，太长玩家会觉得模组死了。
        /// </summary>
        private const int BLOCK_STREAK_BEFORE_RECOVERY = 15;

        /// <summary>手动栈的接管上限：正常一圈用不到，超出说明我们对表的理解已经错了，停手比猜下去安全。</summary>
        private const int MAX_ADOPTED_NODES = 96;

        /// <summary>连续「想写但被闸挡下」的帧数（自愈的计时器）。</summary>
        private int m_blockStreak;

        /// <summary>锚点重绑的累计账（每次重抓快照后累加，统计行直接读它）。</summary>
        private readonly AnchorRebind.Stats m_rebindStats = new AnchorRebind.Stats();

        /// <summary>上一次打过「重绑趋势」那行日志时的累计变化数，用来只在有变化时各记一行。</summary>
        private int m_lastRebindChange = -1;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_watch = new Stopwatch();
            try
            {
                m_areaTool = World.GetOrCreateSystemManaged<AreaToolSystem>();
                m_toolRaycast = World.GetOrCreateSystemManaged<ToolRaycastSystem>();
                m_prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
                m_netCollect = World.GetOrCreateSystemManaged<UpdateCollectSystem>();
                m_sampler = new WorldSampler(EntityManager);
                m_lk = new WorldSampler.SamplerLookups
                {
                    Nodes = GetComponentLookup<Game.Net.Node>(true),
                    Edges = GetComponentLookup<Game.Net.Edge>(true),
                    Curves = GetComponentLookup<Game.Net.Curve>(true),
                    EdgeGeoms = GetComponentLookup<Game.Net.EdgeGeometry>(true),
                    AreaNodes = GetBufferLookup<Game.Areas.Node>(true),
                    // 需求 2/3/5 要的三项数据都挂在 prefab 上，不抓回来引擎就只能按最保守的假设走。
                    PrefabRefs = GetComponentLookup<Game.Prefabs.PrefabRef>(true),
                    PrefabNetGeometry = GetComponentLookup<Game.Prefabs.NetGeometryData>(true),
                    Compositions = GetComponentLookup<Game.Net.Composition>(true),
                    PrefabNetComposition = GetComponentLookup<Game.Prefabs.NetCompositionData>(true),
                    Elevations = GetComponentLookup<Game.Net.Elevation>(true),
                    ObjectTransforms = GetComponentLookup<Game.Objects.Transform>(true),
                    PrefabObjectGeometry = GetComponentLookup<Game.Prefabs.ObjectGeometryData>(true),
                    PrefabBuildingData = GetComponentLookup<Game.Prefabs.BuildingData>(true),
                    Owners = GetComponentLookup<Game.Common.Owner>(true),
                    BuildingFlags = GetComponentLookup<Game.Buildings.Building>(true),
                };
                SnapperLog.Info("[ZoneSnapper] 系统就绪：AreaToolSystem/ToolRaycastSystem/PrefabSystem/UpdateCollectSystem 均已取得");
            }
            catch (Exception e)
            {
                SnapperLog.Error("[ZoneSnapper] 取系统失败，模组不生效：" + e.GetType().Name + " " + e.Message);
                m_areaTool = null;
            }
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
        }


        protected override void OnUpdate()
        {
            // 事件标记每帧都要取走清零，否则「上一帧的提交」会残留到工具被切走的那一帧（Playbook §3.5 同类坑）。
            bool commit = AreaToolPatches.TakeCommit();
            bool pop = AreaToolPatches.TakePop();

            // 总闸：OnLoad 有任何一环没起来（补丁没打上 / 系统没注册）时，本系统一个字节都不许碰。
            // 见 SnapperState.Broken 的注释 —— v0.1.0 就是死在这里没有闸，把玩家的区域工具搞坏了。
            if (SnapperState.Broken) return;

            if (m_areaTool == null) return;
            ZoneSnapperSetting inst = ZoneSnapperSetting.Instance;
            if (inst != null) inst.PollSync();      // 第六轮：滑杆值每帧对指纹，不依赖 setter 是否被 UI 调到
            ModConfig cfg = SnapperState.Config;
            if (cfg == null) cfg = SnapperState.Config = ModConfig.CreateDefault();

            // 快捷键「重算贴合区域」：丢掉缓存，下一次点击起按最新路网重算。
            if (SnapperState.ResetRequested)
            {
                SnapperState.ResetRequested = false;
                ClearSession();
                SnapperLog.Info("[ZoneSnapper] 收到重算请求：会话缓存已清空");
            }

            // 闸门只看「总开关 + 是不是创建态」。
            // v0.1.0 这里还多了一条 `m_areaTool.recreate != Entity.Null ⇒ 返回`，本意是「编辑已有区域时别乱动」，
            // 实际效果是把**放完建筑再画用地**的那整类绘制都挡在贴合之外（填埋场/工业特殊用地就是这条路：
            // FACT：ObjectToolSystem.cs:3992-3998 会把 recreate 设上、mode=Edit）。玩家视角正是本轮反馈的
            // 「垃圾场填埋区能放节点，但模组像没启用一样」。现在改成：照管，但开局先接管游戏灌进来的
            // 现有控制点（ProjectKit.ShouldAdopt），接管时不重新吸附 ⇒ 不会擅自挪玩家已存好的几何。
            if (!cfg.Enabled || m_areaTool.state != AreaToolSystem.State.Create)
            {

                // 拖拽改形（State.Modify）：本系统不碰控制点表 —— 那时表里只有游标一格，
                // 环在游戏别处（FACT：ATS:785/:893 非 Create 态 Length-1 选 0）。
                // 但要给跟随系统留个记号：玩家松手那一帧游戏给这块区域盖 Updated，
                // 那一帧值得马上按最新路网重描一圈（需求 3 的第二种模式）。
                if (cfg.Enabled && m_areaTool.state == AreaToolSystem.State.Modify)
                {
                    // 顺序：先清上一场绘制的会话（含预览），再算这一帧的拖拽预览 ——
                    // 反过来的话 ClearSession 里那句「撤掉预览」会把刚发布的几何一起抹掉。
                    if (m_stack.Count > 0 || m_world != null) ClearSession();
                    SnapperState.DragArmed = true;
                    PreviewDrag(cfg);          // 淡色预览：松手后左右两条边会贴成什么样
                }
                else
                {
                    Idle("tool-idle");
                }
                return;
            }

            try
            {
                Step(cfg, commit, pop);
            }
            catch (Exception e)
            {
                SnapperLog.Error("[ZoneSnapper] 本帧跳过（已兜底，不影响绘制）：" + e.GetType().Name + " " + e.Message);
                ClearSession();
            }
        }

        private void Step(ModConfig cfg, bool commit, bool pop)
        {
            m_watch.Restart();

            NativeList<ControlPoint> cps = m_areaTool.GetControlPoints(out _, out JobHandle listDeps);
            listDeps.Complete();        // 之后主线程读写这张表是安全的（游戏的表本来就在它自己的依赖链上）
            int len = cps.Length;
            if (len == 0)
            {
                Idle("empty-list");
                return;
            }

            AreaTier tier;
            double prefabSnap;
            double minNodeDistance;
            ReadPrefab(cfg, out tier, out prefabSnap, out minNodeDistance);
            ResolvedTuning tuning = PolicyKit.Resolve(cfg, tier, prefabSnap, minNodeDistance);
            if (!tuning.SnapEnabled)
            {
                // 这一档的区域类型被玩家关掉了。注意这里必须连预览一起撤：
                // 老写法只在 m_stack 非空时清会话，「刚放下第一个点就关掉这一类的开关」
                // 会让那条淡色线永久留在地上（第五轮反馈 1 的第二条路径）。
                Idle("tier-off");
                LogOnceTierOff(tier);
                return;
            }

            // 游戏的游标格（它自己吸附后的位置）就是我们的输入：
            // 这样「游戏已经处理好的地块边/轴锁吸附」不会被我们抹掉，我们只在它之上再贴一次。
            Engine.P3 gameCursor = WorldSampler.ToP3(cps[len - 1].m_Position);

            // 拿游戏「当前实际」的那张表来比，而不是拿我们上次写进去的记忆比 ——
            // 游戏每帧自己会写游标格，用记忆比会漏掉它的改动，结果就是「我们看到的是贴好的点，
            // 玩家点下去落定的却是游戏那个漂移后的点」。
            System.Collections.Generic.List<Engine.P3> actual = new System.Collections.Generic.List<Engine.P3>(len - 1);
            for (int i = 0; i < len - 1; i++) actual.Add(WorldSampler.ToP3(cps[i].m_Position));

            // —— 1) 左键提交：把刚刚落定的那一格收进手动栈
            if (commit)
            {
                if (len >= 2)
                {
                    // ⚠ 收到 Apply 事件 ≠ 游戏真的落了点。State.Create 那一支在追加新节点**之前**有一道
                    //    最小间距校验：倒数第二格 ↔ 游标格距离 < AreaUtils.GetMinNodeDistance 时整个提交直接
                    //    什么都不做（FACT：ATS:3509-3511 的 `if (num >= minNodeDistance2)` 没有 else，
                    //    落到 ATS:3567 的 `return Update(...)`）。而 Harmony 的后缀是无条件跑的。
                    //    District 的这道门槛是 32 米 —— 玩家点两下挨得近是再正常不过的操作，
                    //    照事件收点就会把上一个手动节点重复收一遍：环里出现自重叠点、描边算出退化边，
                    //    更要命的是右键撤销要点两下才掉一个可见节点（直接违背需求 3）。
                    //    判据回到证据本身：表的长度真的比我们上一帧写进去的多一格，才算落了点。
                    int prevSlots = m_lastProjection != null ? m_lastProjection.Slots.Count : -1;
                    if (ProjectKit.GameAcceptedCommit(m_stack.Count, prevSlots, actual.Count))
                    {
                        Engine.P3 landed = WorldSampler.ToP3(cps[len - 2].m_Position);
                        SnapperState.Commits++;
                        EnsureWorld(landed, gameCursor, tuning, cfg);
                        PlacedNode node = ManualNode(landed, tier, cfg, tuning);
                        m_stack.Push(node);
                        SnapperState.LastSnapKind = node.Kind.ToString();
                    }
                    else
                    {
                        // 游戏把这一击退了（多半是离上一个节点太近）。我们跟着什么都不做，只是记个数：
                        // 实机看到 rejected 在涨就说明玩家点得比游戏允许的更密。
                        SnapperState.RejectedCommits++;
                    }
                }
            }
            // —— 2) 右键撤销：弹掉一个手动节点（需求 3）。游戏已经把它那一格删了，我们同步栈并整批丢掉那条边的描边点。
            if (pop)
            {
                if (m_stack.PopLast()) SnapperState.Pops++;
            }

            // —— 2.5) 开局接管：我们一个手动节点都没有、而表里已经躺着控制点。
            //          要么这是「放完建筑接着改用地」（游戏把区域现有节点灌进了 m_ControlPoints），
            //          要么提交事件漏收了一次。两种情况下「先照单收下」都比「每帧拒写」有用，
            //          而且收下时**不重新吸附** —— 那些点是玩家已经存好的几何。
            if (ProjectKit.ShouldAdopt(m_stack.Count, len - 1))
            {
                for (int i = 0; i <= len - 2; i++)
                {
                    // 同自愈：**位置原样、锚点照认**。这些点是游戏灌进来的（编辑/重建已有区域时是存档里
                    // 已有的几何），擅自挪位置就是改存档；但若不认锚点，这一圈每一条边都会退回直线，
                    // 玩家视角正是第二轮反馈的「垃圾场填埋区能放节点，但完全没贴合」。
                    m_stack.Push(AnchorRebind.Attribute(
                        new PlacedNode(WorldSampler.ToP3(cps[i].m_Position), SnapKind.Free, -1, -1, 0, 0),
                        m_world, cfg, tuning));
                }
                SnapperState.Adopted += len - 1;
                SnapperLog.Info("[ZoneSnapper] 接管游戏已有的 " + (len - 1) + " 个控制点（编辑/重建或事件漏收；接管的点不重新吸附，只认锚点）");
            }

            // —— 3) 游标贴合（含「点回起点」的闭合处理）
            EnsureWorld(gameCursor, gameCursor, tuning, cfg);
            PlacedNode cur = ManualNode(gameCursor, tier, cfg, tuning);
            if (tier == AreaTier.MapTile)
            {
                if (cur.Kind != SnapKind.Free) SnapperState.SnapHits++; else SnapperState.SnapMisses++;
            }
            SnapperState.LastSnapKind = cur.Kind.ToString();
            if (ProjectKit.IsClosing(m_stack, cur.Pos, Math.Max(0.5, tuning.MinSpacing * 0.5)))
            {
                // 保持「游标 == 第 0 格」：游戏自己靠这个判闭合（FACT：ATS:811-818 + Tooltip.CompleteArea ATS:1895），
                // 我们改了位置就会把游戏的闭合判定弄丢。
                // 锚点信息（Kind/Edge/Arc/Side）整套取第 0 格自己的：闭合边的描边要沿「第 0 格所贴的那条线」
                // 走回去，留着游标那次点击的 Edge/Arc 会让首尾两段各走一条边（v0.1.1 的闭合边折返线即此）。
                PlacedNode first = m_stack.Nodes[0];
                cur = new PlacedNode(first.Pos, first.Kind, first.GraphNode, first.Edge, first.Arc, first.Signature, first.Side);
            }
            m_lastCursorNode = cur;

            // —— 4) 描边（增量：只算签名变了的边，需求 5 的绘制中自动跟随）
            if (m_netCollect != null && m_netCollect.netsUpdated && m_stack.Count > 1)
            {
                m_worldSig = 0;   // 网络变过 ⇒ 缓存的快照作废，下一次 EnsureWorld 重抓
            }
            RetraceStaleEdges(cfg, tuning);

            // —— 5) 投影并写回
            Projection proj = ProjectKit.Build(m_stack, cfg, tuning);
            bool wantWrite = ProjectKit.NeedsWrite(proj, actual, gameCursor, cur.Pos, 0.01);
            // 只有「这一帧我们要比表里少」时才需要「表里全是我们自己写的」这份证据（ProjectKit.MayWrite ②）。
            // 稳态下不分配任何东西 —— 这条路径每帧都跑（Playbook：不要在 ToolUpdate 里 new）。
            bool shrinking = proj.Slots.Count < actual.Count;
            System.Collections.Generic.List<Engine.P3> lastWritten =
                shrinking && m_lastProjection != null ? m_lastProjection.Positions() : null;
            bool mayWrite = ProjectKit.MayWrite(proj, actual, lastWritten, ShrinkEps(tuning));
            if (wantWrite && !mayWrite)
            {
                // 表里有一格不是我们写进去的，而我们这一帧要减点 ⇒ 一定哪里不同步了（漏收提交、表被清过）。
                // 这一帧什么都不写，把工具原样交还给游戏；这个数字只在异常时增长，稳态恒为 0。
                SnapperState.Desyncs++;
                wantWrite = false;
                m_blockStreak++;
                // 但「一直不写」对玩家就是「模组没效果」——v0.1.0 后半程就是这个观感。
                // 连续被挡半秒 ⇒ 把表里多出来的那截尾巴照原样收进手动栈（不重新吸附），下一帧就能接着贴合。
                // 详见 ProjectKit.MissingTail：尾巴上多出来的只可能是我们漏收的那一次左键落点。
                if (m_blockStreak >= BLOCK_STREAK_BEFORE_RECOVERY && m_stack.Count < MAX_ADOPTED_NODES)
                {
                    System.Collections.Generic.List<Engine.P3> tail =
                        ProjectKit.MissingTail(proj, actual, m_stack, Math.Max(0.5, tuning.MinSpacing * 0.5));
                    m_blockStreak = 0;
                    if (tail.Count > 0)
                    {
                        for (int i = 0; i < tail.Count; i++)
                        {
                            // 位置原样收下（那是已经落在表里的几何），但**锚点要认出来**：
                            // 老写法一律塞 SnapKind.Free ⇒ 这些格往后的每一条边都被 TraceKit 判成
                            // NotOnNetwork，自愈反而把「贴合」永久关掉了（第三轮实机 adopted=4 之后
                            // traceHit 就再也不涨，正是这条）。Attribute 只补锚点、不动位置。
                            m_stack.Push(AnchorRebind.Attribute(
                                new PlacedNode(tail[i], SnapKind.Free, -1, -1, 0, 0), m_world, cfg, tuning));
                        }
                        SnapperState.Adopted += tail.Count;
                        SnapperLog.Info("[ZoneSnapper] 自愈：表里比我们多 " + tail.Count +
                                        " 格（漏收的左键提交），已按原样收进手动栈，继续贴合");
                    }
                }
            }
            else if (wantWrite)
            {
                m_blockStreak = 0;
                // 减点写盘只在两种情况下发生：右键撤销（需求 3：手动节点连同那条边的描边点一起走）、
                // 或玩家把简化/弧线滑杆调得更粗。实机看这个数字应当跟 pop= 同量级，明显更大就是我们在乱减。
                if (shrinking) SnapperState.ShrinkWrites++;
            }
            if (wantWrite)
            {
                WriteList(cps, proj, cur);
                // 记的是「真的写进表里的那一帧」的投影 —— 它是下一次左键的基线
                // （ProjectKit.GameAcceptedCommit 拿它比对表长）。被闸挡下、或这帧无需写的时候都不许更新：
                // 用没落地的投影当基线会把「游戏加了一格」这件事比错。
                m_lastProjection = proj;
                m_lastWrittenCursor = cur.Pos;
                SnapperState.AutoNodesWritten = proj.AutoCount;
            }
            // —— 5.5) 玩家把游标点回起点 = 准备闭合：这时才算「含闭合边」的完整环，交给提交后系统补写。
            //          实时投影里不能插闭合描边点——那会占掉倒数第二格，让游戏的最小间距校验
            //          把这最后一击静默吃掉（详见 ZoneSnapperApplySystem 类注释）。
            if (m_stack.Count >= 3 && ProjectKit.IsClosing(m_stack, cur.Pos, Math.Max(0.5, tuning.MinSpacing * 0.5)))
            {
                BuildClosingRing(cfg, tuning, proj);
            }

            // —— 5.7) 实时预览（第四轮反馈 2）：把「这一帧点下去会成什么样」发布给渲染端。
            //          位置就摆在投影写回之后：cur 已经算完、边已经补算过，预览与真实落点用的是同一套结果。
            PublishStrokePreview(cfg, tuning, cur, prefabSnap);

            m_watch.Stop();
            SnapperState.LastBuildMillis = m_watch.Elapsed.TotalMilliseconds;

            // 统计行：实机调参就看这一行（v0.1.0 那份日志被 20 帧一行的刷屏淹掉了，改成 ~5 秒一行，
            // 并把「补丁打上没有」「被闸挡下几次」放进来 —— 这两个数字决定「没效果」是不是我们的锅。
            // slots/list 这一对是专门给 desync>0 时看的：两边不等就是「我们以为的表」和「真表」脱节，
            // 光看 manual 猜不出来。）
            // follow/followSkip/followReject 是第 ⑥ 条（已提交区域跟随）的账：只有总数没有每块的细节，
            // 每块的细节在跟随系统自己那行「跟随重描：区域节点 X → Y 个」里。
            if (++m_staleLogTick >= 300)
            {
                m_staleLogTick = 0;
                SnapperLog.Info(string.Format(
                    "[ZoneSnapper] tier={0} mode={31} radius={1:F1} prefab={9:F1} minNode={15:F1} spacing={16:F1} " +
                    "manual={2} auto={3} slots={17} list={18} snap={4} miss={11} traceHit={5} fallback={6} " +
                    "commit={12} rejected={19} pop={13} shrink={20} adopted={14} hooks={7}/2 desync={8} " +
                    "follow={21} followSkip={22} followReject={23} undo={32} " +
                    "rebind={24} resnap={25} lost={26} stale={27} fb={28} " +
                    "pv={29} pvSkip={30} dragIdx={33} dragPos={34} cont={35} weld={36} " +
                    "attach={37} despike={38} under={39} junc={40} lastMs={10:F2}",
                    tier, tuning.SnapRadius, m_stack.Count, proj.AutoCount,
                    SnapperState.SnapHits, SnapperState.TraceHits, SnapperState.TraceFallbacks,
                    SnapperState.HooksInstalled, SnapperState.Desyncs, prefabSnap,
                    SnapperState.LastBuildMillis, SnapperState.SnapMisses,
                    SnapperState.Commits, SnapperState.Pops, SnapperState.Adopted,
                    minNodeDistance, tuning.MinSpacing, proj.Slots.Count, actual.Count,
                    SnapperState.RejectedCommits, SnapperState.ShrinkWrites,
                    SnapperState.FollowedAreas, SnapperState.FollowSkippedSame, SnapperState.FollowRejected,
                    m_rebindStats.Rebound, m_rebindStats.Resnapped, m_rebindStats.Lost,
                    SnapperState.StaleIndexBlocks, FallbackTally.Summary(),
                    PreviewFeed.Publishes, PreviewFeed.SkipSummary(),
                    ZoneSnapperSetting.ModeName(cfg.Mode), SnapperState.UndoDepth,
                    SnapperState.DragProbeByIndex, SnapperState.DragProbeByPosition,
                    SnapperState.Continuations, SnapperState.AdoptedBorderNodes,
                    SnapperState.AttachedPoints, SnapperState.DespikedJoints, SnapperState.UndergroundDropped,
                    SnapperState.JunctionClamps));
            }

        }

        private Engine.P3 m_lastWrittenCursor;

        /// <summary>闭合环的认领窗口（帧）。见 ClearSession 里那段帧序自杀的注释。</summary>
        private const int PENDING_RING_FRAMES = 8;

        /// <summary>
        /// 「表里这一格是不是我们写的」的比对容差。不能照抄 NeedsWrite 的 1 厘米：
        /// 游戏在我们写完之后还会跑一次它自己的 SnapControlPoints 挪点（FACT：ATS:3552-3557），
        /// 落点可以比一厘米远得多，但它仍然是**我们放进去的那一格**。取间距的四分之一、且不低于 1 米。
        /// </summary>
        private static double ShrinkEps(ResolvedTuning tuning)
        {
            return Math.Max(1.0, tuning.MinSpacing * 0.25);
        }

        /// <summary>
        /// 第六轮反馈 3：**手动节点（含游标）除城市边界外不再由模组吸附**。
        /// 游戏内置的节点对齐本来就会把手动点吸到中心线/边缘（它的吸附距离由下面
        /// <see cref="ReadPrefab"/> 里写进 prefab 的那一份控制），我们再吸一次只会把点拽到
        /// "我们觉得更好"的候选上 —— 实机症状正是反馈 1 的三条：空地上吸到鼠标旁边的节点、
        /// 莫名其妙换一条路、有时干脆没反应。模组的吸附从这一轮起只作用于**自动贴合路径上的点**。
        /// 城市边界例外：游戏对它完全不给吸附（GetAvailableSnapMask 全关），不吸就等于没功能。
        /// </summary>
        private PlacedNode ManualNode(Engine.P3 raw, AreaTier tier, ModConfig cfg, ResolvedTuning tuning)
        {
            if (tier == AreaTier.MapTile && cfg.SnapMapTile && tuning.SnapEnabled)
                return SnapKit.FindSnap(raw, m_world, cfg, tuning, default(PlacedNode), SnapContext());
            return SnapKit.FreeNode(raw);
        }

        /// <summary>读当前工具的区域类型与游戏自己配的两个尺度：贴合半径、最小节点间距（需求 7 的基准都是它们）。</summary>
        private void ReadPrefab(ModConfig cfg, out AreaTier tier, out double prefabSnap, out double minNodeDistance)
        {
            tier = AreaTier.Other;
            prefabSnap = 0;
            minNodeDistance = 0;
            try
            {
                AreaPrefab prefab = m_areaTool.prefab;
                if (prefab == null) return;
                AreaGeometryData data = m_prefabSystem.GetComponentData<AreaGeometryData>(prefab);
                switch (data.m_Type)
                {
                    case Game.Areas.AreaType.District: tier = AreaTier.District; break;
                    case Game.Areas.AreaType.Lot: tier = AreaTier.Lot; break;
                    case Game.Areas.AreaType.Surface: tier = AreaTier.Surface; break;
                    case Game.Areas.AreaType.Space: tier = AreaTier.Space; break;
                    case Game.Areas.AreaType.MapTile: tier = AreaTier.MapTile; break;
                    default: tier = AreaTier.Other; break;
                }
                prefabSnap = data.m_SnapDistance;

                // 第八轮反馈 3/10：**这里一个字都不再写回 prefab**。
                // 第六轮反馈 3 曾把滑杆倍率写进 m_SnapDistance，理由是「滑杆要连游戏内置对齐一起管」；
                // 第八轮玩家把这条要求反过来说了两次：手动节点归游戏的对齐工具管，模组不许碰。
                // 写这个数等于**改游戏对齐的强度**（FACT：ATS:185-193、:353、:792-888 全从
                // areaGeometryData.m_SnapDistance 取），拉到 300% 时玩家点哪都被拽过去 ——
                // 症状正是反馈 3/10 抱怨的「我点的位置被模组挪到路上」。
                // 副作用还有一条：同一个数决定区域边框虚线的粗细与段长
                //（FACT：Game.Rendering/AreaBorderRenderSystem.cs:544-552、:378-395），
                // 也就是第六轮那个「越来越粗的虚线长方形」的根源。不写 ⇒ 根源没了。
                // ⇒ 滑杆从这一版起**只作用于模组自己的自动点**（半径走 PolicyKit.Resolve 那份倍率，
                //   游戏对齐那一档保持原版手感）。
                // 最小节点间距照游戏自己的口径算，不跟滑杆走：
                //（FACT：AreaUtils.GetMinNodeDistance = m_SnapDistance * 0.5f）
                minNodeDistance = prefabSnap > 0.01 ? prefabSnap * 0.5 : 0;
            }
            catch (Exception e)
            {
                SnapperLog.Warn("[ZoneSnapper] 读 AreaGeometryData 失败，按 Other 档处理：" + e.GetType().Name);
            }
        }

        // —————————————————————————— （已删除）prefab 吸附距离的"借与还"
        //
        // 第六轮到第七轮这里有一套 borrow/restore：把 滑杆倍率×原版值 写进 AreaGeometryData.m_SnapDistance，
        // 并在「离开创建态 / 关总开关 / 系统销毁」三个时机还回去。第八轮反馈 3/10 把它整个撤掉（理由见
        // ReadPrefab 里那段）。删干净而不是留着不写：留着就是三处调用点 + 一张反射缓存 + 一条日志，
        // 而它管的还是「玩家点下去落在哪」这种最不该由我们决定的东西。
        // 撤掉之后连带的好处：城市边界那种粗虚线方块再也没有我们能拧粗的机会（第六轮反馈 2 的排查结论）。

        /// <summary>
        /// 保证手里的世界快照覆盖「这一次绘制的全部」。走廊不够大或网络变脏才重抓 ——
        /// 抓一次要过四叉树，每帧抓是 Playbook 点名的性能杀手。
        ///
        /// 【第四轮实机改的口径】走廊**只长不缩**，并且始终含住每一个已放下的角（见 PolicyKit.PlanStroke）。
        /// 老写法每帧只按「本段两端」算走廊：玩家往远处的拐角点一下，走廊跳过去，
        /// 早先那个角所在的路就被丢出快照 ⇒ 那两条边永远判 NotOnNetwork ⇒ 退回直线。
        /// 玩家视角正是本轮反馈的「有的路会贴、有的不会，很随机」
        /// （账：fb=NotOnNetwork×121、resnap=45、lost=25，而 stale=0 —— 锚点没越界，是路整个不在快照里了）。
        /// </summary>
        private void EnsureWorld(Engine.P3 a, Engine.P3 b, ResolvedTuning tuning, ModConfig cfg)
        {
            if (m_world != null && m_worldSig != 0 && m_worldBox.Contains(a) && m_worldBox.Contains(b)) return;

            m_anchorPts.Clear();
            for (int i = 0; i < m_stack.Nodes.Count; i++) m_anchorPts.Add(m_stack.Nodes[i].Pos);
            PolicyKit.StrokePlan plan = PolicyKit.PlanStroke(m_anchorPts, a, b, tuning.SnapRadius, m_strokeRaw);
            if (!plan.Capture.IsValid) return;
            m_strokeRaw = plan.Raw;

            if (plan.Clamped && !m_warnedClamp)
            {
                m_warnedClamp = true;
                SnapperLog.Warn("[ZoneSnapper] 这一圈已经大到走廊上限 " + (int)PolicyKit.STROKE_LIMIT
                                + " 米：早期节点所在的路可能不在快照里，那几条边会退回直线（不是坏了，是尺寸边界）");
            }

            RefreshLookups();
            // 只有贴外沿的那几档才需要建筑轮廓（市辖区贴中心线，抓了也用不上）⇒ 少一次物体树遍历。
            m_world = m_sampler.Capture(plan.Capture, true,
                PolicyKit.UsesEdgeTargets(tuning.Tier), m_lk);
            m_worldBox = plan.Capture;
            m_worldSig = m_world.ComputeSignature();
            // —— 锚点先搬回新快照的下标空间，再作废描边。顺序很重要：描边读的就是这些下标，
            //    先重绑后算这一帧就能描对；先算后绑等于白算一遍（而且算出来全是 NotOnNetwork）。
            //    这一句是第三轮实机（snap=2907 却 traceHit=17 / fallback=119）的正面修复，
            //    根因与口径见 Engine/AnchorRebind.cs 的类注释。
            if (m_stack.Count > 0)
            {
                AnchorRebind.Stack(m_stack, m_world, cfg, tuning, m_rebindStats);
                int changed = m_rebindStats.Rebound + m_rebindStats.Resnapped + m_rebindStats.Lost;
                if (changed != m_lastRebindChange)
                {
                    // 只在「账有变化」时各记一行：这条是给我们看趋势的，不该刷屏。
                    m_lastRebindChange = changed;
                    SnapperLog.Info(string.Format(
                        "[ZoneSnapper] 重抓快照后锚点重绑：搬回下标 {0} 次、换认目标 {1} 次、认不出退回自由点 {2} 次（位置一律不动）",
                        m_rebindStats.Rebound, m_rebindStats.Resnapped, m_rebindStats.Lost));
                }
            }
            if (m_stack.Count > 1) m_stack.MarkAllStale();
        }

        /// <summary>
        /// 闭合边描边：从最后一个手动节点描回起点，产出真正落档的那一圈节点。
        /// 同一个手动栈只算一次（SnapperState.SetPendingRing 内部按栈签名去重），
        /// 否则玩家把鼠标停在起点上不动时会每帧跑一次图搜索。
        /// </summary>
        private void BuildClosingRing(ModConfig cfg, ResolvedTuning tuning, Projection proj)
        {
            if (m_world == null || m_stack.Count < 3) return;
            // 玩家把鼠标停在起点上等着点闭合时，这段每帧都会进来 —— 不加这道去重就是
            // 「每帧一次图搜索 + 每帧一条日志」，正是 Playbook 点名的写战循环形状。
            if (SnapperState.HasPendingRingFor(m_stack.Signature)) return;
            TraceContext ctx = new TraceContext();
            TraceContextUtil.Fill(m_stack.Nodes, ref ctx);
            TraceResult closing = TraceKit.Trace(
                m_stack.Nodes[m_stack.Count - 1], m_stack.Nodes[0], m_world, cfg, tuning, ctx);
            if (closing.UsedNetwork || closing.UsedBorder) SnapperState.TraceHits++;
            else SnapperState.TraceFallbacks++;
            if (closing.Continued) SnapperState.Continuations++;      // 闭合边也可能是续接出来的（反馈 7）
            if (closing.Attached > 0) SnapperState.AttachedPoints += closing.Attached;   // 第八轮反馈 2/3/10
            if (closing.Clamped > 0) SnapperState.JunctionClamps += closing.Clamped;     // 反馈 5：拐在路口中心那一个点上
            System.Collections.Generic.List<Engine.P3> ring = ProjectKit.BuildClosedRing(m_stack, closing, cfg, tuning);
            SnapperState.SetPendingRing(ring, proj.Slots.Count + 1, m_stack.Signature);
            SnapperLog.Info(string.Format(
                "[ZoneSnapper] 闭合预览：环点数 {0}（闭合边描出 {1} 个点，回退原因 {2}）",
                ring.Count, closing.Points.Count, closing.Fallback));
        }

        // —————————————————————————— 实时预览（第四轮反馈 2）
        //
        // 生产端在这里，渲染端在 ZoneSnapperPreviewSystem（Rendering 相位）。两边靠 PreviewFeed 交接。
        // 三条共同口径：
        //  ① 预览**一个字节都不写**进控制点表与区域数据 —— 它随时会因为玩家移动鼠标而作废；
        //  ② 任何一次异常只关掉预览（ClearSession 之外不再兜底），不能让锦上添花伤到主链路；
        //  ③ 每帧算，但带预算闸（PreviewKit.BUDGET_MS）：上一帧整步超预算就沿用上一帧那套几何。

        /// <summary>绘制中的预览：最后一格 → 游标那一段，加上将要落下的那一格与会被插入的自动节点。</summary>
        private void PublishStrokePreview(ModConfig cfg, ResolvedTuning tuning, PlacedNode cursor, double prefabSnap)
        {
            try
            {
                PreviewKit.Result r = PreviewKit.Stroke(m_stack.Nodes, cursor, m_world, cfg, tuning,
                    SnapperState.LastBuildMillis);
                StylePreview(r, prefabSnap);
                PreviewFeed.Publish(r);
            }
            catch (Exception e)
            {
                PreviewFeed.Clear("error");
                LogOncePreview(e);
            }
        }

        /// <summary>
        /// 拖拽中的预览（第二种模式）：抓着已存在区域的一格还没松手 ⇒ 淡色画出「松手后左右两条边会贴成什么样」。
        ///
        /// 【下标与实体都不靠猜】FACT（反编译 AreaToolSystem）：
        ///  · :3440 进 State.Modify 的前提就是 <c>HasComponent&lt;Area&gt;(cp.m_OriginalEntity) &amp;&amp; math.any(cp.m_ElementIndex >= 0)</c>
        ///    ⇒ <c>m_OriginalEntity</c> 是那块区域、<c>m_ElementIndex.x</c> 是它在 <c>Game.Areas.Node</c> 缓冲里的格号；
        ///  · :842-848 游戏自己也是这么用的：<c>m_Nodes[cp.m_OriginalEntity][cp.m_ElementIndex.x]</c>，
        ///    左右邻格按 :845/:846 的环绕算法取（我们这里取整圈后交给 FollowKit.Around，口径一致）；
        ///  · :3678 <c>index = math.select(0, Length-1, state==Create)</c> ⇒ Modify 态表里第 0 格就是跟着鼠标动的那一格，
        ///    而且已经过游戏自己的吸附（:3681 SnapControlPoints）。我们在它之上再贴一次，不抢游戏的活。
        /// </summary>
        private void PreviewDrag(ModConfig cfg)
        {
            try
            {
                if (!cfg.ShowLivePreview) { PreviewFeed.Clear("preview-off"); return; }

                JobHandle deps;
                NativeList<ControlPoint> moveStart;
                NativeList<ControlPoint> cps = m_areaTool.GetControlPoints(out moveStart, out deps);
                deps.Complete();
                if (cps.Length < 1)
                {
                    PreviewFeed.Clear("no-cp");
                    return;
                }

                AreaTier tier;
                double prefabSnap;
                double minNodeDistance;
                ReadPrefab(cfg, out tier, out prefabSnap, out minNodeDistance);
                ResolvedTuning tuning = PolicyKit.Resolve(cfg, tier, prefabSnap, minNodeDistance);
                Engine.P3 live = WorldSampler.ToP3(cps[0].m_Position);

                Entity area = Entity.Null;
                int vi = -1;
                if (moveStart.IsCreated && moveStart.Length > 0)
                {
                    ControlPoint start = moveStart[0];
                    if (start.m_OriginalEntity != Entity.Null && start.m_ElementIndex.x >= 0)
                    {
                        area = start.m_OriginalEntity;
                        vi = start.m_ElementIndex.x;
                        SnapperState.DragProbeByIndex++;
                    }
                    else
                    {
                        // 表里第 0 格没带实体：整张起点表里找一格带 Area 实体的（插入新节点那一支就是这形状）。
                        for (int i = 0; i < moveStart.Length; i++)
                        {
                            Entity cand = moveStart[i].m_OriginalEntity;
                            if (cand == Entity.Null) continue;
                            if (!EntityManager.HasComponent<Game.Areas.Area>(cand)) continue;
                            area = cand;
                            if (moveStart[i].m_ElementIndex.x >= 0) vi = moveStart[i].m_ElementIndex.x;
                            break;
                        }
                    }
                }
                if (area != Entity.Null && !EntityManager.HasComponent<Game.Areas.Area>(area))
                {
                    area = Entity.Null;
                    vi = -1;
                }
                SnapperState.DragTarget = area;
                SnapperState.DragTargetIndex = vi;
                if (area == Entity.Null) { PreviewFeed.Clear("not-a-vertex"); return; }
                if (!m_lk.AreaNodes.HasBuffer(area)) { PreviewFeed.Clear("no-ring"); return; }

                DynamicBuffer<Game.Areas.Node> buf = m_lk.AreaNodes[area];
                if (buf.Length < 3) { PreviewFeed.Clear("ring-too-small"); return; }

                m_dragRing.Clear();
                for (int i = 0; i < buf.Length; i++)
                {
                    m_dragRing.Add(WorldSampler.ToP3(buf[i].m_Position));
                }
                // 游戏的环有两种写法：环形索引，或末点重复首点（FACT：ATS:1499 生成瓦片时重复首点）。
                // 与跟随系统同一口径：收成「不重复首点」的圈，闭合由 FollowKit 负责。
                bool closed = true;
                if (m_dragRing.Count > 1 && m_dragRing[m_dragRing.Count - 1].DistanceTo(m_dragRing[0]) <= 0.05)
                {
                    m_dragRing.RemoveAt(m_dragRing.Count - 1);
                    if (vi >= m_dragRing.Count) vi = -1;
                }
                if (vi < 0)
                {
                    // 位置兜底：离鼠标这一格最近的那个存档节点，就是玩家正在拖的那一个。
                    vi = NearestRingNode(live, System.Math.Max(16.0, prefabSnap * 1.5));
                    if (vi < 0) { PreviewFeed.Clear("not-a-vertex"); return; }
                    SnapperState.DragProbeByPosition++;
                    SnapperState.DragTargetIndex = vi;
                }
                if (vi >= m_dragRing.Count) { PreviewFeed.Clear("dup-vertex"); return; }

                int pi = (vi + m_dragRing.Count - 1) % m_dragRing.Count;
                int ni = (vi + 1) % m_dragRing.Count;
                EnsureDragWorld(live, m_dragRing[pi], m_dragRing[ni], tuning, cfg);

                PreviewKit.Result r = PreviewKit.Drag(m_dragRing, closed, vi, live, m_dragWorld, cfg, tuning,
                    SnapperState.LastBuildMillis);
                StylePreview(r, prefabSnap);
                PreviewFeed.Publish(r);
            }
            catch (Exception e)
            {
                PreviewFeed.Clear("error");
                LogOncePreview(e);
            }
        }

        /// <summary>
        /// 位置兜底用的「这一圈里离这点最近的那一格」；超过 <paramref name="limit"/> 米返回 -1 ——
        /// 太远说明玩家其实没在拖这一圈的任何一格，宁可不画也不要猜一个格号去改他的区域。
        /// 判据形状抄游戏自己：它在 Modify 态也是拿 <c>buffer[j].m_Position</c> 与控制点逐格比对
        /// （FACT：AreaToolSystem.cs:2814-2832），只是它要求精确相等、我们按距离（因为我们没有起点那一格）。
        /// </summary>
        private int NearestRingNode(Engine.P3 p, double limit)
        {
            int best = -1;
            double bestD = double.MaxValue;
            for (int i = 0; i < m_dragRing.Count; i++)
            {
                double d = m_dragRing[i].DistanceTo(p);
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best < 0 || bestD > limit) return -1;
            return best;
        }

        /// <summary>预览几何自身的线宽/点径：跟着游戏自己给这类区域的吸附距离缩放，不自己定绝对值。</summary>
        private static void StylePreview(PreviewKit.Result r, double prefabSnap)
        {
            if (r == null || prefabSnap <= 0) return;
            r.LineWidth = (float)Math.Max(0.15, prefabSnap * 0.06);
            r.DotDiameter = (float)Math.Max(0.5, prefabSnap * 0.25);
        }

        private void LogOncePreview(Exception e)
        {
            if (m_previewLogged) return;
            m_previewLogged = true;
            SnapperLog.Warn("[ZoneSnapper] 实时预览这一帧没算出来（不影响贴合与落档）：" +
                            e.GetType().Name + " " + e.Message);
        }

        /// <summary>
        /// 拖拽专用的走廊快照：与绘制中同款单调走廊（<see cref="PolicyKit.PlanStroke"/>），
        /// 但**状态完全独立** —— 绘制会话的 m_world 在 Modify 态是空的，共用一套字段会让
        /// 「松手后立刻重描」和「拖拽预览」互相把对方的快照清掉，退化成每帧抓一次四叉树。
        /// 走廊只包住「被拖格 + 左右邻格」这三点附近：拖一个角不需要把整块地的路网再抓一遍。
        /// 只有这三点不再被同一走廊包住（或路网刚变过）才重抓。
        /// </summary>
        private void EnsureDragWorld(Engine.P3 live, Engine.P3 prev, Engine.P3 next, ResolvedTuning tuning, ModConfig cfg)
        {
            bool dirty = m_netCollect != null && m_netCollect.netsUpdated;
            if (!dirty && m_dragWorld != null && m_dragBox.Contains(live) && m_dragBox.Contains(prev) && m_dragBox.Contains(next))
            {
                return;
            }
            m_dragAnchors.Clear();
            m_dragAnchors.Add(prev);
            m_dragAnchors.Add(next);
            PolicyKit.StrokePlan plan = PolicyKit.PlanStroke(m_dragAnchors, live, prev, tuning.SnapRadius, m_dragRaw);
            if (!plan.Capture.IsValid) return;
            m_dragRaw = plan.Raw;
            RefreshLookups();
            m_dragWorld = m_sampler.Capture(plan.Capture, true, PolicyKit.UsesEdgeTargets(tuning.Tier), m_lk);
            m_dragBox = plan.Capture;
        }

        /// <summary>
        /// 贴合用的上下文：区域质心 + 上一个手动节点。需求 3 的两条判断都要它们
        /// （一条路有左右两条外沿 ⇒ 选离质心近的那条；产业区/垃圾场不许跨越马路或建筑）。
        /// 手动栈为空时两者都不成立，退化成「这一次点击独立判贴合」—— 老行为，不是漏装。
        /// </summary>
        private SnapKit.SnapExtra SnapContext()
        {
            SnapKit.SnapExtra x = new SnapKit.SnapExtra();
            if (m_stack != null && m_stack.Count >= 1)
            {
                x.HasPrevious = true;
                x.Previous = m_stack.Nodes[m_stack.Count - 1];
            }
            TraceContext ctx = new TraceContext();
            TraceContextUtil.Fill(m_stack.Nodes, ref ctx);
            x.HasCentroid = ctx.HasCentroid;
            x.Centroid = ctx.Centroid;
            return x;
        }

        /// <summary>
        /// ComponentLookup 在结构性变更后会过期，每次抓取前刷新（Entities 1.3 的刷新口是 Update(SystemBase)，
        /// 没有 UpdateLookup —— 见 WorldSampler.SamplerLookups 的注释）。
        /// </summary>
        private void RefreshLookups()
        {
            m_lk.Nodes.Update(this);
            m_lk.Edges.Update(this);
            m_lk.Curves.Update(this);
            m_lk.EdgeGeoms.Update(this);
            m_lk.AreaNodes.Update(this);
            m_lk.PrefabRefs.Update(this);
            m_lk.PrefabNetGeometry.Update(this);
            m_lk.Compositions.Update(this);
            m_lk.PrefabNetComposition.Update(this);
            m_lk.Elevations.Update(this);
            m_lk.ObjectTransforms.Update(this);
            m_lk.PrefabObjectGeometry.Update(this);
            m_lk.PrefabBuildingData.Update(this);
            m_lk.Owners.Update(this);
            m_lk.BuildingFlags.Update(this);
        }

        /// <summary>逐条边补算：签名对得上就复用缓存（增量大算，不是定时全量）。</summary>
        private void RetraceStaleEdges(ModConfig cfg, ResolvedTuning tuning)
        {
            if (!tuning.TraceEnabled || m_stack.Count < 2 || m_world == null) return;
            int budget = MAX_RETRACE_PER_FRAME;
            for (int i = 0; i < m_stack.Edges.Count && budget > 0; i++)
            {
                if (m_stack.EdgeIsFresh(i, m_worldSig)) continue;
                budget--;
                TraceContext ctx = new TraceContext();
                TraceContextUtil.Fill(m_stack.Nodes, ref ctx);
                // 第 i 条边替换的就是手动环上「第 i 格 → 第 i+1 格」那一格；报给描边，
                // 自交判定才是在「换掉这一格之后的边界」上做的，而不是拿环 + 一条重复弦去比。
                if (i + 1 < m_stack.Count) { ctx.HasRingEdge = true; ctx.RingEdge = i; }
                PlacedNode a = m_stack.Nodes[i];
                PlacedNode b = m_stack.Nodes[i + 1];
                TraceResult r = TraceKit.Trace(a, b, m_world, cfg, tuning, ctx);
                m_stack.SetEdge(i, r);
                m_stack.StampEdge(i, m_worldSig);
                if (r.UsedNetwork || r.UsedBorder) SnapperState.TraceHits++;
                else if (r.Fallback != TraceFallback.Disabled)
                {
                    SnapperState.TraceFallbacks++;
                    FallbackTally.Note(r.Fallback);     // 到底是哪一条闸挡的，实机只看总数猜不出修法
                    if (r.Fallback == TraceFallback.NotOnNetwork && m_world != null &&
                        (a.Edge >= m_world.Edges.Count || b.Edge >= m_world.Edges.Count))
                    {
                        SnapperState.StaleIndexBlocks++;
                    }
                }
                // 反馈 7：续接另记一间。⚠ 必须记在这条 if/else 链**之后** ——
                // 续接是一条**成功**的描边（UsedNetwork=true、Fallback=None），塞进链里会把 else 绑到它身上，
                // 于是每一次成功的非续接描边都被算成一次回退（这种账错了，实机就只能照着错账去改引擎）。
                if (r.Continued) SnapperState.Continuations++;
                // 第八轮反馈 2/3/10：走线自己长出来的接入点另记一间（attach 与 cont 是此消彼长的那一对，
                // 见 SnapperState.AttachedPoints 的注释）。同样必须记在这条 if/else 链之后。
                if (r.Attached > 0) SnapperState.AttachedPoints += r.Attached;
                // 反馈 5：交付顶点里有多少格被挪到了路口的中心点（junc= 与 attach= 一起看，
                // 见 SnapperState.JunctionClamps 的注释）。
                if (r.Clamped > 0) SnapperState.JunctionClamps += r.Clamped;
                // 反馈 7：与邻接区域共线的那一段抄了多少个节点坐标（逐位相同才算，见 AdoptBorderNodes）
                if (r.Adopted > 0) SnapperState.AdoptedBorderNodes += r.Adopted;
                SnapperState.RetraceRuns++;
            }
        }

        /// <summary>
        /// 写回游戏的控制点表。顺序严格是 [手动/自动槽位…, 游标]，游标那一格沿用游戏自己那份的元数据，
        /// 只改位置：m_OriginalEntity / m_ElementIndex 之类游戏在「编辑既有区域」时会用（本模组 v0.1
        /// 在 recreate 态直接不介入，见 OnUpdate 的闸）。
        /// </summary>
        private void WriteList(NativeList<ControlPoint> cps, Projection proj, PlacedNode cursor)
        {
            ControlPoint cursorProto = cps[cps.Length - 1];
            float3 cur = WorldSampler.ToFloat3(cursor.Pos);
            cursorProto.m_Position = cur;
            cursorProto.m_HitPosition = cur;

            cps.Clear();
            for (int i = 0; i < proj.Slots.Count; i++)
            {
                Slot s = proj.Slots[i];
                cps.Add(MakePoint(s.Pos));
            }
            cps.Add(cursorProto);
        }

        private static ControlPoint MakePoint(Engine.P3 p)
        {
            float3 v = WorldSampler.ToFloat3(p);
            ControlPoint cp = new ControlPoint
            {
                m_Position = v,
                m_HitPosition = v,
                m_Direction = new float2(0f, 1f),
                m_HitDirection = new float3(0f, 1f, 0f),
                m_Rotation = quaternion.identity,
                m_OriginalEntity = Entity.Null,
                m_SnapPriority = new float2(0f, 0f),
                m_ElementIndex = new int2(-1, -1),
                m_CurvePosition = 0f,
                m_Elevation = 0f
            };
            return cp;
        }

        /// <summary>
        /// 「这一帧我们什么都不做」的唯一出口。
        ///
        /// 【为什么单独一个方法】第五轮反馈 1：只放了一个节点就按 ESC 退出工具，淡色的预览线和节点
        /// 还留在地上。根因是这类提前返回各写各的 —— 有的清会话、有的什么都不清，
        /// 「这一帧我们什么都不做」的统一出口（第五轮反馈 1）。
        ///
        /// 原来每条提前返回各写各的：有的清会话、有的什么都不清。预览信箱只要没人 Clear，
        /// 渲染端就会把**最后一帧**的淡色几何一直画下去 —— 玩家看到的就是
        /// 「第二个节点还没放就退出工具，预览的线和节点还留在地上」。
        /// 现在所有提前返回都必须走这里：会话状态、拖拽快照、预览信箱一起撤。
        /// 渲染端另外还有一道新鲜度闸（<see cref="ZoneSnapperPreviewSystem.STALE_FRAMES"/>），
        /// 防的是「连本系统都不再跑」那种情况 —— 那时这里根本没机会执行。
        /// </summary>
        private void Idle(string why)
        {
            if (m_stack.Count > 0 || m_world != null || m_dragWorld != null || PreviewFeed.Current != null)
            {
                // ClearSession 里已经 PreviewFeed.Clear("session-end")：那一格就是这条路径的判读依据。
                ClearSession();
            }
            else PreviewFeed.Clear(why);
        }

        /// <summary>「这一类被关掉了」只说一句，别每帧刷（Playbook：日志刷屏会淹掉下一次事故的证据）。</summary>
        private void LogOnceTierOff(AreaTier tier)
        {
            if (m_tierOffLogged == tier) return;
            m_tierOffLogged = tier;
            SnapperLog.Info("[ZoneSnapper] 类别 " + tier + " 的开关是关的 ⇒ 本模组完全不介入该工具"
                            + "（游戏自带的节点吸附仍然会吸到路上，那不是本模组做的）");
        }

        private AreaTier m_tierOffLogged = (AreaTier)(-99);

        /// <summary>
        /// 清会话。工具切走 / 关闭开关 / 出异常时都走这里。
        /// 只清我们自己的状态，不去改游戏的表 —— 游戏自己会在 Apply/Cancel/模式切换里 Clear（FACT：ATS:3067-3075、ATS:3502）。
        /// </summary>
        private void ClearSession()
        {

            m_stack.Clear();
            m_world = null;
            m_worldBox = Box2.Empty();
            m_strokeRaw = Box2.Empty();
            m_warnedClamp = false;
            m_worldSig = 0;
            m_lastProjection = null;
            m_lastWrittenCursor = default(Engine.P3);
            m_blockStreak = 0;
            // 第六轮根因（闭合边六轮零次「已补写」的帧序自杀）：闭合点击那一帧工具离开 Create，
            // 本系统紧随 AreaToolSystem 跑 Idle→ClearSession，把**刚登记**的闭合环丢掉；
            // 而同帧更晚的 ApplyTool 相位里 ApplySystem 才来认领 ⇒ 永远认领不到。
            // 改成给闭合环一个 8 帧的认领窗口：窗口内清会话不丢环，ApplySystem 认领成功或窗口过期才丢。
            if (SnapperState.PendingRingAge() > PENDING_RING_FRAMES)
            {
                SnapperState.DiscardPendingRing();
            }
            // 预览跟着会话一起撤：工具都切走了还把一条淡色线留在地上，玩家会以为区域长那样。
            PreviewFeed.Clear("session-end");
            m_dragWorld = null;
            m_dragBox = Box2.Empty();
            m_dragRaw = Box2.Empty();
            m_dragRing.Clear();
            m_previewLogged = false;
        }

        /// <summary>切开关时由 Setting 调用：立刻把上一会话的残留状态丢掉。</summary>
        public void ForceReset()
        {
            ClearSession();
        }
    }
}
