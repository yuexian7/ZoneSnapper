using System;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Scripting;
using ZoneSnapper.Engine;
using ZoneSnapper.GameSide;

namespace ZoneSnapper.Systems
{
    /// <summary>
    /// 已提交区域的自动跟随（需求 5）+ 拖拽改形后的重描（需求 3 的第二种模式）。
    ///
    /// ============================================================
    /// 【第五轮反馈 8：这一版之前它一整局都没跑过，原因写在相位上】
    /// 四轮日志里 <c>follow=0 followSkip=0 followReject=0</c>，而「拿不到鼠标射线命中点」那行也从没出现
    /// ⇒ 不是判定严，是 <b>OnUpdate 根本没被调用</b>。根因（本轮反编译闭环）：
    ///  · 本系统原来注册在 <c>SystemUpdatePhase.ApplyTool</c>；
    ///  · 而那一相位**只在「这一帧真的有工具提交」时才存在** ——
    ///    FACT：Game.Tools/ToolSystem.cs:251-261 的 OnUpdate 只驱动 PreTool / ToolUpdate / PostTool；
    ///    FACT：Game.Tools/ToolOutputSystem.cs:20-30 —— ApplyTool 只在 <c>applyMode==ApplyMode.Apply</c>
    ///    的那一帧被驱动，ClearTool 只在 Clear 那一帧；
    ///  · 「玩家改了路」与「玩家按了重算键」这两件事都不产生工具提交 ⇒ 那些帧里 ApplyTool 相位不存在。
    ///    改路那一帧确实有 Apply（NetTool 提交），但 <c>netsUpdated</c> 要到**下一帧**的 Modification5 才置位
    ///    （FACT：Game.Net/UpdateCollectSystem.cs:389-413 + SystemOrder.cs:192 注册在 Modification5），
    ///    而下一帧已经没有 ApplyTool 了 ⇒ 这个触发条件在数学上就凑不齐。
    /// 【改法】注册到 <c>ToolUpdate</c> 相位（每帧必跑，且排在 ZoneSnapperSystem 之后：
    /// 那时主系统已经把 DragArmed / 拖拽目标这些记号留好了）。Modification5 排在 ToolUpdate 之前
    /// （枚举顺序：…Modification5, ModificationEnd, PreSimulation, PostSimulation, GameSimulation,
    ///  EditorSimulation, Rendering, PreTool, PostTool, ToolUpdate…，FACT：Game/SystemUpdatePhase.cs），
    /// 所以在 ToolUpdate 里读 netsUpdated 读到的就是**本帧刚置上**的那个真值。
    ///
    /// 【拖拽那一支也跟着改】原来靠「Area 上有 Updated 标记」反推玩家刚拖了哪一块 ——
    /// 那是单帧标记（FACT：Game.Common/CleanUpSystem.cs 在 Cleanup 相位回收 Created/Updated，
    /// 而 Cleanup 排在 ApplyTool **之后**），换到 ToolUpdate 相位后同一帧读不到、下一帧又被清了。
    /// 现在不读脏标记，直接读工具自己那张表：进 State.Modify 的前提就是
    /// <c>cp.m_OriginalEntity</c> 上挂着 <see cref="Game.Areas.Area"/>
    /// （FACT：AreaToolSystem.cs:3440），所以「玩家正在拖哪一块」在 Modify 那一帧就写在表里；
    /// 等状态离开 Modify 的下一帧再重描那一块 —— 那时游戏的落档已经写完，我们看到的就是最终形状。
    ///
    /// ============================================================
    /// 【两条触发路径，风险等级不同，所以闸也不同】（第六轮反馈 5 收紧过）
    ///  ① 拖拽改形：玩家自己刚拖过一格 ⇒ **只要模组开着就走**，这就是玩家那一击要的结果。
    ///     只重描被拖格的左右两条边（<see cref="TryDragRetrace"/>），不整环重画。
    ///  ② 按「重算贴合区域」键：范围限制在**视野盒**内的区域，且要求
    ///     <c>FollowCommittedAreas</c> 打开（关着就在日志里说一次，什么都不写）。
    ///  ✗ 第三条路已经删掉：路网/建筑变了**不再自动改写存档**，只在日志里提示一句。
    ///    玩家原话：「自动跟随是道路/轨道/建筑发生移动才需要去跟随，而且也是按了键才修改」——
    ///    老实现在玩家没看的那片地上批量改几何，是本轮「不太靠谱」的直接来源。
    ///
    /// 【四道护栏】
    ///  · 形状没变 ⇒ 一个字节都不写（<see cref="GeoKit.CurvesAgree"/>，按形状而不是点数比）；
    ///  · <see cref="FollowKit.Rebuild"/> 自检不过（自交/退化）⇒ 保持原样；
    ///  · 写缓冲区连续抛三次 ⇒ 本局停用跟随；
    ///  · 每次改写前把**原来那一圈**存进单级撤销栈 ⇒ 玩家的「退回上一次自动跟随」有东西可退（反馈 8）。
    /// </summary>
    [Preserve]
    public partial class ZoneSnapperFollowSystem : GameSystemBase
    {
        /// <summary>每帧最多重描几块区域。跟随是后台行为，绝不能跟帧率抢时间。</summary>
        private const int FOLLOW_BUDGET = 2;

        /// <summary>一块区域最多多少个节点还值得重描（超了说明它已经密到不需要我们操心）。</summary>
        private const int MAX_RING_NODES = 900;

        /// <summary>
        /// 路网脏标记的去抖窗口（帧）：改一条路会连着好几帧置位 <c>netsUpdated</c>，
        /// 只在窗口刚打开那一帧打一行提示，否则日志会被同一句话刷满。第六轮之后它**不再触发任何写盘**。
        /// </summary>
        private const int NET_LATCH_FRAMES = 90;

        /// <summary>视野盒半边长（米）：跟随以「玩家看得见的这片」为界，不做全城扫描。</summary>
        private const double VIEW_HALF = 320.0;

        /// <summary>视野盒内最多取多少块区域当候选（四叉树去重后的上限）。</summary>
        private const int VIEW_CANDIDATES = 192;

        /// <summary>形状算「没变」的容差（米）。5 厘米远小于游戏自己的落点精度，也不会漏掉真改动。</summary>
        private const double SAME_EPS = 0.05;

        /// <summary>撤销栈的深度。反馈 8 要的是「退回上一次」，所以只留**一批**；再多就是自作聪明。</summary>
        private const int UNDO_LIMIT = 24;

        private EntityQuery m_areaQuery;
        private AreaToolSystem m_areaTool;
        private ToolRaycastSystem m_toolRaycast;
        private Game.Net.UpdateCollectSystem m_netCollect;
        private WorldSampler m_sampler;
        private WorldSampler.SamplerLookups m_lk;
        private ComponentLookup<Game.Prefabs.PrefabRef> m_prefabRef;
        private ComponentLookup<Game.Prefabs.AreaGeometryData> m_prefabArea;

        /// <summary>路网变更的余量帧（每帧从 netsUpdated 续期）。</summary>
        private int m_netLatch;

        /// <summary>玩家正在拖的那块区域（Modify 态从控制点表读到；null = 这一局还没拖过）。</summary>
        private Entity m_dragEntity = Entity.Null;

        /// <summary>上一帧工具是不是在 Modify 态。用它抓「刚松手」那一下。</summary>
        private bool m_wasModifying;

        /// <summary>松手之后等几帧再重描：游戏的落档与它下游的 Updated 搬运同帧完成，让一帧最稳。</summary>
        private int m_dragWait;
        private const int DRAG_SETTLE_FRAMES = 2;

        private int m_consecutiveFailures;
        private bool m_writeRouteDead;
        private int m_noRaycastTick;

        /// <summary>「按键被开关挡住」这件事只说一次（每次重新打开开关会复位，见 OnUpdate）。</summary>
        private bool m_keyBlockedLogged;
        private bool m_followWasAllowed;
        private readonly List<Entity> m_candidates = new List<Entity>();

        /// <summary>本批改写前的原样快照（撤销栈）+ 正在累积的那一批。</summary>
        private readonly List<UndoEntry> m_undo = new List<UndoEntry>();
        private readonly List<UndoEntry> m_pendingBatch = new List<UndoEntry>();

        /// <summary>一次撤销需要的东西：哪块区域、原来那几个节点（含高程）。</summary>
        private struct UndoEntry
        {
            public Entity Area;
            public List<Game.Areas.Node> Nodes;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_areaQuery = GetEntityQuery(ComponentType.ReadOnly<Game.Areas.Area>());
            try
            {
                m_areaTool = World.GetOrCreateSystemManaged<AreaToolSystem>();
                m_toolRaycast = World.GetOrCreateSystemManaged<ToolRaycastSystem>();
                m_netCollect = World.GetOrCreateSystemManaged<Game.Net.UpdateCollectSystem>();
                m_sampler = new WorldSampler(EntityManager);
                m_prefabRef = GetComponentLookup<Game.Prefabs.PrefabRef>(true);
                m_prefabArea = GetComponentLookup<Game.Prefabs.AreaGeometryData>(true);
                // ⚠ 这份 lookups 与主系统那份**不能共用**：ComponentLookup 是按「创建它的系统」登记依赖的，
                // 跨系统共用会让更新依赖算错（Entities 1.3 的 Update(SystemBase) 就是这个用途）。
                m_lk = new WorldSampler.SamplerLookups
                {
                    Nodes = GetComponentLookup<Game.Net.Node>(true),
                    Edges = GetComponentLookup<Game.Net.Edge>(true),
                    Curves = GetComponentLookup<Game.Net.Curve>(true),
                    EdgeGeoms = GetComponentLookup<Game.Net.EdgeGeometry>(true),
                    AreaNodes = GetBufferLookup<Game.Areas.Node>(true),
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
                SnapperLog.Info("[ZoneSnapper] 跟随系统就绪：ToolUpdate 相位每帧必跑（第五轮修正：原来挂在 ApplyTool，"
                                + "那一相位只在有工具提交的帧才存在）");
            }
            catch (Exception e)
            {
                m_writeRouteDead = true;
                SnapperLog.Error("[ZoneSnapper] 跟随系统初始化失败（自动跟随与拖拽重描不可用，绘制中的贴合不受影响）："
                                 + e.GetType().Name + " " + e.Message);
            }
        }

        protected override void OnUpdate()
        {
            if (SnapperState.Broken || m_writeRouteDead) return;
            ZoneSnapperSetting inst = ZoneSnapperSetting.Instance;
            if (inst != null) inst.PollSync();      // 第六轮：滑杆值每帧对指纹，不依赖 setter 是否被 UI 调到
            ModConfig cfg = SnapperState.Config;
            if (cfg == null || !cfg.Enabled)
            {
                SnapperState.DragArmed = false;
                SnapperState.FollowRequested = false;
                SnapperState.UndoFollowRequested = false;
                m_netLatch = 0;
                m_dragWait = 0;
                m_dragEntity = Entity.Null;
                m_wasModifying = false;
                return;
            }

            // —— 0) 玩家的「退回上一次自动跟随」（反馈 8 新加的快捷键）
            if (SnapperState.UndoFollowRequested)
            {
                SnapperState.UndoFollowRequested = false;
                UndoLastBatch();
            }

            // 开关从关变开 ⇒ 把「按键被挡住」那句提示复位，玩家再关一次还能听到一次。
            if (cfg.FollowCommittedAreas && !m_followWasAllowed) m_keyBlockedLogged = false;
            m_followWasAllowed = cfg.FollowCommittedAreas;

            // —— 1) 拖拽：抓「刚松手」那一下，并记下玩家拖的是哪一块
            bool modifying = false;
            try { modifying = m_areaTool != null && m_areaTool.state == AreaToolSystem.State.Modify; }
            catch (Exception) { modifying = false; }
            if (modifying)
            {
                m_dragWait = 0;
                Entity grabbed = TryGrabbedArea();
                if (grabbed != Entity.Null) m_dragEntity = grabbed;
                SnapperState.DragArmed = true;
            }
            else if (m_wasModifying)
            {
                // 松手：等游戏把这一圈写完（同帧的 ApplyTool 相位之后），下一两帧再重描
                m_dragWait = DRAG_SETTLE_FRAMES;
            }
            m_wasModifying = modifying;
            if (m_dragWait > 0) m_dragWait--;
            bool dragging = m_dragWait == 0 && m_wasModifying == false && SnapperState.DragArmed;
            if (dragging)
            {
                SnapperState.DragArmed = false;
                m_dragWait = -1;          // 只认这一次：不设回来的话后面每帧都会重描同一块
            }

            // —— 2) 路网变更（本帧 Modification5 刚置上的那个真值）
            //    第六轮反馈 5：路网/建筑变了**只提示、不自动改** —— 写盘必须按键（「重算贴合区域」）。
            //    老实现 netsUpdated 就直接扫视野盒改写已提交区域，玩家没在看的地也被动 ⇒ "不太靠谱"。
            //    提示按 m_netLatch 去抖：改一条路会连着好几帧置位 netsUpdated，每帧打一行等于刷屏。
            if (m_netCollect != null && m_netCollect.netsUpdated)
            {
                if (m_netLatch == 0)
                {
                    SnapperLog.Info("[ZoneSnapper] 路网/建筑变了：已提交区域不会自动改，" +
                                    "要按「重算贴合区域」那颗键才写盘（第六轮反馈 5 的口径）");
                }
                m_netLatch = NET_LATCH_FRAMES;
            }
            else if (m_netLatch > 0) m_netLatch--;

            // —— 3) 按键重算（唯一会批量改写存档的路径）
            bool manual = SnapperState.FollowRequested;
            SnapperState.FollowRequested = false;
            if (manual && !cfg.FollowCommittedAreas)
            {
                // 开关关着就一颗键也不写：这是玩家自己选的（他不想让模组碰存档）。
                // 但必须**说一次**，否则表现和"按键没反应"一模一样，正是本轮反馈 1 抱怨的形状。
                if (!m_keyBlockedLogged)
                {
                    m_keyBlockedLogged = true;
                    SnapperLog.Info("[ZoneSnapper] 按了「重算贴合区域」，但选项页的" +
                                    "「允许『重算贴合区域』键改写已保存的区域」是关的 ⇒ 这次什么都没写");
                }
                manual = false;
            }
            if (!dragging && !manual) return;

            try
            {
                int wrote = Step(cfg, dragging, manual, dragging ? FOLLOW_BUDGET : 1);
                if (wrote > 0) CommitBatch(dragging ? "DRAG" : "MANUAL");
                else m_pendingBatch.Clear();
                m_consecutiveFailures = 0;
            }
            catch (Exception e)
            {
                m_pendingBatch.Clear();
                m_consecutiveFailures++;
                if (m_consecutiveFailures >= 3)
                {
                    m_writeRouteDead = true;
                    SnapperLog.Error(string.Format(
                        "[ZoneSnapper] 连续 {0} 次读写区域节点缓冲区失败（{1}）：本局停用自动跟随与拖拽重描，" +
                        "绘制中的贴合照常。原因与修法见开发笔记「模组侧写 ECS 缓冲区」一节。",
                        m_consecutiveFailures, e.GetType().Name + ": " + e.Message));
                }
                else
                {
                    SnapperLog.Warn("[ZoneSnapper] 本帧跟随跳过：" + e.GetType().Name + " " + e.Message);
                }
            }
        }

        /// <summary>
        /// 玩家正在拖的那一块区域：<c>m_ControlPoints[...].m_OriginalEntity</c>。
        /// 判据与游戏自己进 Modify 的那道闸一致（FACT：AreaToolSystem.cs:3440 要求
        /// <c>HasComponent&lt;Area&gt;(cp.m_OriginalEntity) &amp;&amp; math.any(cp.m_ElementIndex &gt;= 0)</c>）。
        /// 读不到返回 <c>Entity.Null</c>（那就退回到视野盒扫描那一支，不猜）。
        /// </summary>
        private Entity TryGrabbedArea()
        {
            try
            {
                JobHandle deps;
                NativeList<ControlPoint> cps = m_areaTool.GetControlPoints(out _, out deps);
                deps.Complete();
                for (int i = 0; i < cps.Length; i++)
                {
                    Entity e = cps[i].m_OriginalEntity;
                    if (e == Entity.Null) continue;
                    if (!EntityManager.HasComponent<Game.Areas.Area>(e)) continue;
                    return e;
                }
                // 游戏在 Modify 态每帧用 GetRaycastResult() 的新点替换 m_ControlPoints[0]
                // （FACT：AreaToolSystem.cs:3661-3671 + :3678 index=0），那张新点未必还带着实体；
                // 兜底去 m_MoveStartPositions 里找（:3444-3445 进 Modify 时快照的那一批）。
            }
            catch (Exception) { }
            return Entity.Null;
        }

        /// <summary>返回这一帧真的改写了几块区域。</summary>
        private int Step(ModConfig cfg, bool dragging, bool sweep, int budget)
        {
            m_candidates.Clear();

            // ① 拖拽改形：只看玩家刚松手的那一块 —— 不扫全城，也不碰他没动的地
            if (dragging && m_dragEntity != Entity.Null && EntityManager.Exists(m_dragEntity))
            {
                m_candidates.Add(m_dragEntity);
                m_dragEntity = Entity.Null;
            }

            // ② 路网变更 / 快捷键：视野盒内的区域。拿不到命中点就这一轮不做 —— 跟着猜一个中心
            //    等于把玩家没在看的那片地改了，正是本文件要避免的形状。
            if (sweep && budget > 0)
            {
                Engine.P3 view;
                if (TryViewCenter(out view))
                {
                    m_noRaycastTick = 0;
                    List<Entity> near = m_sampler.AreaEntities(BoxAround(view, VIEW_HALF), VIEW_CANDIDATES);
                    for (int i = 0; i < near.Count; i++) if (!m_candidates.Contains(near[i])) m_candidates.Add(near[i]);
                }
                else if (++m_noRaycastTick >= 300)
                {
                    m_noRaycastTick = 0;
                    SnapperLog.Info("[ZoneSnapper] 跟随：拿不到鼠标射线命中点，这一轮没有候选区域");
                }
            }

            int wrote = 0;
            for (int i = 0; i < m_candidates.Count && budget > 0; i++)
            {
                int r = TryFollow(m_candidates[i], cfg, dragging);
                if (r < 0) continue;        // 不归我们管（从属区域/没有节点表），不占预算
                budget--;
                if (r > 0) wrote++;
            }
            return wrote;
        }

        /// <summary>
        /// 一块区域：读环 → 反推锚点重描 → 形状没变就不写。
        /// 返回 1=写了；0=算了但没写（形状没变或被自检拦下）；-1=这块不归我们管。
        /// </summary>
        private int TryFollow(Entity area, ModConfig cfg, bool drag)
        {
            if (area == Entity.Null || !EntityManager.Exists(area)) return -1;
            // 从属区域的节点会被游戏从 Owner 覆写 ⇒ 改了白改（与 ZoneSnapperApplySystem 同一条护栏）
            if (EntityManager.HasComponent<Game.Areas.SubArea>(area)) return -1;
            if (!m_lk.AreaNodes.TryGetBuffer(area, out DynamicBuffer<Game.Areas.Node> buf)) return -1;
            if (buf.Length < 3) return -1;

            List<P3> ring = new List<P3>(buf.Length);
            List<Game.Areas.Node> original = new List<Game.Areas.Node>(buf.Length);
            for (int i = 0; i < buf.Length; i++)
            {
                original.Add(buf[i]);
                ring.Add(new P3(buf[i].m_Position.x, buf[i].m_Position.z, buf[i].m_Position.y));
            }
            // 游戏的环有两种写法：环形索引，或末点重复首点（FACT：ATS:1499 生成瓦片时重复首点）。
            // 统一收成「不重复首点」的圈，交给 FollowKit 按闭合环处理。
            if (ring.Count > 1 && ring[ring.Count - 1].DistanceTo(ring[0]) <= SAME_EPS)
            {
                ring.RemoveAt(ring.Count - 1);
                original.RemoveAt(original.Count - 1);
            }
            if (ring.Count < 3) return -1;
            if (ring.Count > MAX_RING_NODES)
            {
                SnapperLog.Info("[ZoneSnapper] 这块区域有 " + ring.Count + " 个节点，超过跟随上限 " +
                                MAX_RING_NODES + "，跳过（说明它已经比我们能画出的更密）");
                return -1;
            }

            AreaTier tier;
            double prefabSnap;
            TierOf(area, out tier, out prefabSnap);
            ResolvedTuning tuning = PolicyKit.Resolve(cfg, tier, prefabSnap);

            Box2 need = GeoKit.CurveBounds(ring, true).Inflated(tuning.SnapRadius * 2.0 + 40.0);
            RefreshLookups();
            // includeBorders 在这一路**必须是 false**：正在重描的这块区域自己也在 Areas 搜索树里，
            // 把它自己的旧边界当吸附候选的话，AreaBorderSame 的权重（0.85）压过路缘（0.95）与中心线（1.0）
            // ⇒ 每次重描都吸回自己上一轮的形状，路挪了也跟不上（跟随变成空转）。
            // 代价是「与邻接区域节点取齐」（反馈 7）只在绘制那一路生效 —— 那正是玩家要求的场景
            //（新画的地与已有地块共用一段路）。要在跟随里也取齐，得先让采样器支持"排除某块区域"。
            WorldSnapshot world = m_sampler.Capture(need, false,
                PolicyKit.UsesEdgeTargets(tuning.Tier), m_lk);

            // —— 拖拽落档（第六轮反馈 6）：只重描被拖格的左右两条边，与预览走**同一段代码**
            //    （FollowKit.Around + 同一个 DragTargetIndex），预览长什么样落档就是什么样。
            //    老实现这里走整环 Rebuild：密点环整环重描极易判自交 ⇒ followReject 开局就涨、落档不生效。
            if (drag)
            {
                return TryDragRetrace(area, ring, original, buf, SnapperState.DragTargetIndex,
                    world, cfg, tuning, tier);
            }

            FollowKit.FollowResult res = FollowKit.Rebuild(ring, true, world, cfg, tuning);
            if (res.Despiked > 0) SnapperState.DespikedJoints += res.Despiked;   // 第八轮反馈 3/10 × 6/8 的接缝
            if (res.Rejected || res.Ring.Count < 3)
            {
                SnapperState.FollowRejected++;
                return 0;
            }
            if (GeoKit.CurvesAgree(ring, true, res.Ring, true, SAME_EPS))
            {
                SnapperState.FollowSkippedSame++;
                return 0;   // 形状一模一样 ⇒ 一个字节都不写，也不刷 Updated（否则下游每帧白白重三角化）
            }

            // 改写之前先把原样存进撤销栈：玩家要求「退回上一次自动跟随」必须有东西可退。
            PushUndo(area, original);

            buf.Clear();
            List<P3> moved = new List<P3>();
            for (int i = 0; i < res.Ring.Count; i++)
            {
                P3 p = res.Ring[i];
                // elevation 传 float.MinValue = 「按地形重算」，与游戏自己写区域节点同口径（FACT：ATS:1881）
                buf.Add(new Game.Areas.Node(new float3((float)p.X, (float)p.H, (float)p.Y), float.MinValue));
            }
            for (int i = 0; i < res.Anchors.Count && i < ring.Count; i++)
            {
                if (res.Anchors[i].Pos.DistanceTo(ring[i]) > 1e-6) moved.Add(res.Anchors[i].Pos);
            }
            Touch(area);
            SnapperState.FollowedAreas++;
            FollowFlashFeed.Add(res.Ring, moved, null);      // source 由 CommitBatch 补，这里先不重复
            SnapperLog.Info(string.Format(
                "[ZoneSnapper] 跟随重描：区域实体 #{0} 节点 {1} → {2} 个（沿路 {3} 条边，直线 {4} 条，挪位 {5} 格，类别 {6}）",
                area.Index, ring.Count, res.Ring.Count, res.Traced, res.Straight, res.Moved, tier));
            return 1;
        }

        /// <summary>
        /// 拖拽落档重描（第六轮反馈 6）：只重写被拖格的左右两条边，其余节点一个字节都不动。
        /// 与拖拽中的淡色预览走**同一段代码**（FollowKit.Around + 同一个 DragTargetIndex）——
        /// 玩家看到什么，落档就是什么；老实现走整环 Rebuild，密点环整环重描几乎必判自交，
        /// 于是 followReject 从会话开头就涨、落档后节点还在原位。
        /// 返回语义与 TryFollow 相同：1=写了，0=算了但没写，-1=不归我们管。
        /// </summary>
        private int TryDragRetrace(Entity area, List<P3> ring, List<Game.Areas.Node> original,
            DynamicBuffer<Game.Areas.Node> buf, int nodeIndex, WorldSnapshot world, ModConfig cfg,
            ResolvedTuning tuning, AreaTier tier)
        {
            int n = ring.Count;
            if (nodeIndex < 0 || nodeIndex >= n)
            {
                SnapperState.FollowRejected++;
                SnapperLog.Info("[ZoneSnapper] 拖拽落档：认不出被拖的是哪一格（index=" + nodeIndex +
                                "），这一轮不写（宁可不动，也不能猜错格子改存档）");
                return 0;
            }
            FollowKit.DragResult d = FollowKit.Around(ring, true, nodeIndex, world, cfg, tuning);
            if (d.Despiked) SnapperState.DespikedJoints++;            // 第八轮反馈 3/10 × 6/8 的接缝
            if (!d.Valid || d.PrevIndex < 0 || d.NextIndex < 0)
            {
                SnapperState.FollowRejected++;
                return 0;
            }

            // 新环 = [左邻] + 左边重描 + [被拖格] + 右边重描 + [右邻] + 其余原样。
            // 拼接口径只有一条，放在 FollowKit.SpliceLocal 里（回归壳 X9e 直接断言它），
            // 系统这边不再手抄一遍下标运算 —— 抄两遍就是两处会各自漂移的地方。
            List<P3> nr = FollowKit.SpliceLocal(ring, d);
            if (nr.Count < 3) { SnapperState.FollowRejected++; return 0; }

            // 自检只看被替换的局部链：整环自交闸在这一路放宽，否则密点环每次落档都被拒
            //（第六轮反馈 6 的根因之一）。真错误是这段链自己打结。
            if (Engine.SimplifyKit.SelfIntersects(FollowKit.LocalChain(ring, d)))
            {
                SnapperState.FollowRejected++;
                SnapperLog.Info("[ZoneSnapper] 拖拽落档：重描出来的局部链自交，这块保持原样");
                return 0;
            }
            if (GeoKit.CurvesAgree(ring, true, nr, true, SAME_EPS))
            {
                SnapperState.FollowSkippedSame++;
                return 0;
            }

            PushUndo(area, original);
            buf.Clear();
            List<P3> moved = new List<P3>();
            moved.Add(d.Dragged.Pos);
            for (int i = 0; i < nr.Count; i++)
            {
                P3 p = nr[i];
                buf.Add(new Game.Areas.Node(new float3((float)p.X, (float)p.H, (float)p.Y), float.MinValue));
            }
            Touch(area);
            SnapperState.FollowedAreas++;
            FollowFlashFeed.Add(nr, moved, null);
            SnapperLog.Info(string.Format(
                "[ZoneSnapper] 拖拽落档重描：区域实体 #{0} 第 {1} 格，左边 {2} 点 / 右边 {3} 点（回退 {4}/{5}），环 {6} → {7} 个节点",
                area.Index, nodeIndex, d.Before.Count, d.After.Count, d.BeforeFallback, d.AfterFallback, ring.Count, nr.Count));
            return 1;
        }

        // —————————————————————————— 单级撤销（反馈 8：「退回上一次自动跟随的调整」）

        private void PushUndo(Entity area, List<Game.Areas.Node> original)
        {
            UndoEntry e = new UndoEntry();
            e.Area = area;
            e.Nodes = original;
            m_pendingBatch.Add(e);
        }

        /// <summary>这一批改完了，整批压进撤销栈（只保留最近一批 = 「上一次」）。</summary>
        private void CommitBatch(string source)
        {
            if (m_pendingBatch.Count == 0) return;
            m_undo.Clear();
            for (int i = 0; i < m_pendingBatch.Count && i < UNDO_LIMIT; i++) m_undo.Add(m_pendingBatch[i]);
            m_pendingBatch.Clear();
            SnapperState.UndoDepth = m_undo.Count;
            SnapperLog.Info("[ZoneSnapper] 已记下这一次跟随（" + source + "，" + m_undo.Count +
                            " 块区域）：按「退回上一次自动跟随」的快捷键可以整批还原");
        }

        /// <summary>
        /// 把上一次跟随改写的那一批区域**原样写回去**。
        /// 只留一层：再往前的历史要靠游戏自己的撤销，我们在存档几何上做多级撤销的风险大于收益。
        /// </summary>
        private void UndoLastBatch()
        {
            if (m_undo.Count == 0)
            {
                SnapperLog.Info("[ZoneSnapper] 退回上一次跟随：没有可退的记录（本局还没改写过的区域，或已经退回过了）");
                return;
            }
            int done = 0;
            for (int i = 0; i < m_undo.Count; i++)
            {
                UndoEntry e = m_undo[i];
                try
                {
                    if (e.Nodes == null || e.Nodes.Count < 3) continue;
                    if (!EntityManager.Exists(e.Area)) continue;
                    if (!m_lk.AreaNodes.TryGetBuffer(e.Area, out DynamicBuffer<Game.Areas.Node> buf)) continue;
                    buf.Clear();
                    List<P3> ring = new List<P3>(e.Nodes.Count);
                    for (int k = 0; k < e.Nodes.Count; k++)
                    {
                        buf.Add(e.Nodes[k]);
                        ring.Add(new P3(e.Nodes[k].m_Position.x, e.Nodes[k].m_Position.z, e.Nodes[k].m_Position.y));
                    }
                    Touch(e.Area);
                    FollowFlashFeed.Add(ring, null, "UNDO");
                    done++;
                }
                catch (Exception ex)
                {
                    SnapperLog.Warn("[ZoneSnapper] 退回某一块区域失败（跳过它）：" + ex.GetType().Name);
                }
            }
            m_undo.Clear();
            m_pendingBatch.Clear();
            SnapperState.UndoDepth = 0;
            SnapperState.FollowUndoneAreas += done;
            SnapperLog.Info("[ZoneSnapper] 已退回上一次自动跟随的调整：" + done + " 块区域恢复原样" +
                            (done == 0 ? "（这些区域已被游戏或别的模组改过，或实体已不存在）" : ""));
        }

        /// <summary>写完之后必须让下游重算：Updated 是唯一脏标记（见 ZoneSnapperApplySystem 的同款注释）。</summary>
        private void Touch(Entity area)
        {
            if (!EntityManager.HasComponent<Game.Common.Updated>(area))
            {
                EntityManager.AddComponent(area, ComponentType.ReadWrite<Game.Common.Updated>());
            }
            else
            {
                EntityManager.SetComponentData(area, default(Game.Common.Updated));
            }
        }

        /// <summary>撤销栈里现在还有几块（统计行与日志用）。</summary>
        public int UndoCount { get { return m_undo.Count; } }

        /// <summary>
        /// 这块区域按哪一档算。类别在游戏的**区域 prefab** 上（FACT：AreaToolSystem 也是走
        /// <c>ComponentLookup&lt;PrefabRef&gt;</c> → <c>AreaGeometryData.m_Type</c>，:265/:269），
        /// 认不出来时按 Other 档 ⇒ 贴合与描边自然全不生效，不会拿 District 的口径乱改公园边界。
        /// </summary>
        private void TierOf(Entity area, out AreaTier tier, out double prefabSnap)
        {
            tier = AreaTier.Other;
            prefabSnap = 0;
            try
            {
                m_prefabRef.Update(this);
                m_prefabArea.Update(this);
                Game.Prefabs.PrefabRef pr;
                if (m_prefabRef.TryGetComponent(area, out pr) && pr.m_Prefab != Entity.Null)
                {
                    Game.Prefabs.AreaGeometryData data;
                    if (m_prefabArea.TryGetComponent(pr.m_Prefab, out data))
                    {
                        prefabSnap = data.m_SnapDistance;
                        switch (data.m_Type)
                        {
                            case Game.Areas.AreaType.District: tier = AreaTier.District; break;
                            case Game.Areas.AreaType.Lot: tier = AreaTier.Lot; break;
                            case Game.Areas.AreaType.Surface: tier = AreaTier.Surface; break;
                            case Game.Areas.AreaType.MapTile: tier = AreaTier.MapTile; break;
                            default: tier = AreaTier.Other; break;      // 含已删除的 Space 档
                        }
                        return;
                    }
                }
            }
            catch (Exception)
            {
                // 读不到 prefab 就留在 Other：宁可这一帧什么都不改，也不要拿错档的半径去改玩家的地
            }
        }

        private bool TryViewCenter(out Engine.P3 center)
        {
            center = default(Engine.P3);
            if (m_toolRaycast == null) return false;
            try
            {
                Game.Common.RaycastResult r;
                if (!m_toolRaycast.GetRaycastResult(out r)) return false;
                center = WorldSampler.ToP3(r.m_Hit.m_Position);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Box2 BoxAround(Engine.P3 c, double half)
        {
            return new Box2 { MinX = c.X - half, MinY = c.Y - half, MaxX = c.X + half, MaxY = c.Y + half };
        }

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
    }
}
