using System;
using System.Collections.Generic;
using Colossal.Collections;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Unity.Collections;
using Unity.Jobs;
using Unity.Entities;
using Unity.Mathematics;
using ZoneSnapper.Engine;

namespace ZoneSnapper.GameSide
{
    /// <summary>
    /// 世界几何取样器：唯一允许把游戏 ECS 数据翻译成引擎 WorldSnapshot 的地方。
    ///
    /// 为什么要有这一层：引擎层（Engine/）必须一个游戏类型都不碰才能离线跑回归（Playbook 硬规则 14），
    /// 所以「四叉树抓取 + 贝塞尔采样 + 干湿网格」全部收在这里，出参是纯数据结构。
    ///
    /// 性能口径（Playbook 步骤 3.4 的教训：大成本来自 Dependency.Complete() + 全城 ToEntityArray）：
    ///  · 只抓「走廊」——两端点扩出的包围盒，不抓全城；
    ///  · 四叉树依赖先 Complete 一次，之后全程主线程只读，不再调度 job；
    ///  · 采样步长固定，走廊再长单边点数也被 MAX_SAMPLES_PER_EDGE 夹住。
    /// </summary>
    internal sealed class WorldSampler
    {
        /// <summary>曲线离散化步长（米）。2 米足够让 RDP 简化后仍贴着路沿，又不至于一条边产生上百点。</summary>
        private const double SAMPLE_STEP = 2.0;

        /// <summary>单条边最多采样多少个点。</summary>
        private const int MAX_SAMPLES_PER_EDGE = 64;

        /// <summary>
        /// 外沿折线的采样步长（米）。比中心线细一档是因为需求 3 里贴路缘的四种区域
        /// 「稍微有点空隙就很难看」：折线自身的采样误差会直接变成节点密度上限
        /// （引擎在折线上量弦高，量不到的那部分就等于允许可见的空隙）。
        /// </summary>
        private const double EDGE_SAMPLE_STEP = 1.0;

        /// <summary>
        /// 一条走廊里最多抓多少栋建筑的轮廓。密集市中心 200 米走廊能圈进几百个物体，
        /// 而玩家一次点击只关心手边那一两栋 ⇒ 到了上限就停，剩下的用中心线/路缘档兜住。
        /// </summary>
        private const int OBJECT_LIMIT = 96;

        /// <summary>
        /// 取样要读的组件句柄包。由 ZoneSnapperSystem 在 OnCreate 里用 GetComponentLookup&lt;T&gt; 建好、
        /// 每帧 Update(this) 刷新后传进来：这个 Entities 版本的 ComponentLookup&lt;T&gt; 只有
        /// 单参构造 + Update(SystemBase)/Update(ref SystemState) + TryGetComponent，
        /// 没有 UpdateLookup()/Dispose()（FACT：ilspycmd -t Unity.Entities.ComponentLookup`1 的成员清单），
        /// 所以句柄必须挂在系统上，不能在普通类里自建。
        /// </summary>
        internal struct SamplerLookups
        {
            public ComponentLookup<Game.Net.Node> Nodes;
            public ComponentLookup<Game.Net.Edge> Edges;
            public ComponentLookup<Game.Net.Curve> Curves;
            public ComponentLookup<Game.Net.EdgeGeometry> EdgeGeoms;
            public BufferLookup<Game.Areas.Node> AreaNodes;
            /// <summary>边实例 → 路网 prefab（拿 <c>NetGeometryData.m_MergeLayers</c> 判类型变化）；
            /// 建筑实例用的也是这一个（同一个组件类型，没必要建两份 lookup）。</summary>
            public ComponentLookup<Game.Prefabs.PrefabRef> PrefabRefs;
            /// <summary>路网 prefab → 几何数据（Layer 位）。</summary>
            public ComponentLookup<Game.Prefabs.NetGeometryData> PrefabNetGeometry;
            /// <summary>边实例 → 组合（<c>m_Edge</c> 指向 prefab 边）。</summary>
            public ComponentLookup<Game.Net.Composition> Compositions;
            /// <summary>prefab 边 → 组合数据（<c>m_Width</c> 路面宽、<c>m_Flags</c> 高架/隧道）。</summary>
            public ComponentLookup<Game.Prefabs.NetCompositionData> PrefabNetComposition;
            /// <summary>边实例 → 两端高程（只用于诊断）。</summary>
            public ComponentLookup<Game.Net.Elevation> Elevations;
            /// <summary>建筑实例的落地位置与朝向。</summary>
            public ComponentLookup<Game.Objects.Transform> ObjectTransforms;
            /// <summary>prefab → 落地轮廓包围盒（需求 3 的「同一建筑边缘」）。</summary>
            public ComponentLookup<Game.Prefabs.ObjectGeometryData> PrefabObjectGeometry;
            /// <summary>prefab → 地块尺寸（游戏自己在 ObjectSide 档里用它替换 bounds.xz）。</summary>
            public ComponentLookup<Game.Prefabs.BuildingData> PrefabBuildingData;
            /// <summary>子建筑 → 主建筑（「同一栋楼」判据）。</summary>
            public ComponentLookup<Game.Common.Owner> Owners;
            /// <summary>实例上有没有 Building 标记（走 Owner 链找根建筑时的终点判据）。</summary>
            public ComponentLookup<Game.Buildings.Building> BuildingFlags;
        }

        private readonly EntityManager m_em;

        /// <summary>「白名单挡下了多少个非建筑物体」这句日志一局只说一次（每次抓走廊都说等于刷屏）。</summary>
        private bool m_nonBuildingLogged;

        /// <summary>这一次抓取里有多少栋**建筑因为在地下**没进快照（第八轮反馈 3；Capture 末尾并进 UndergroundDropped）。</summary>
        private int m_lastUndergroundObjects;

        public WorldSampler(EntityManager entityManager)
        {
            m_em = entityManager;
        }


        /// <summary>走廊 = 两端点连线扩出一个半径。区域越大走廊越大，与需求 7 的分级一致。</summary>
        public static Box2 CorridorBox(Engine.P3 a, Engine.P3 b, double inflate, double maxInflate)
        {
            Box2 box = Box2.Empty();
            box.Expand(a);
            box.Expand(b);
            double span = Math.Max(a.DistanceTo(b), 1.0);
            double r = Math.Min(inflate + span * 0.35, Math.Max(inflate, maxInflate));
            return box.Inflated(r);
        }

        /// <summary>
        /// 抓一张走廊快照。
        /// 原来这里还有第三个开关 <c>includeShoreline</c>（海岸线折线）：v0.1.4 按玩家要求整条功能去掉了 ——
        /// 「那个开关我完全没发现效果」= 一次水面/地形网格采样换来了零可感知收益，少一个分支少一分代价。
        /// </summary>
        public WorldSnapshot Capture(Box2 corridor, bool includeBorders, bool includeObjects, SamplerLookups lk)
        {
            WorldSnapshot world = new WorldSnapshot();

            NativeList<Entity> edgeEntities = new NativeList<Entity>(Allocator.TempJob);
            NativeList<Entity> nodeEntities = new NativeList<Entity>(Allocator.TempJob);
            try
            {
                CollectNetEntities(corridor, edgeEntities, nodeEntities);
                BuildNodesAndEdges(world, nodeEntities, edgeEntities, lk);
            }
            finally
            {
                edgeEntities.Dispose();
                nodeEntities.Dispose();
            }

            if (includeBorders) CollectAreaBorders(world, corridor, lk);
            // 需求 3：产业区/地皮/表面类贴「道路边缘或同一建筑边缘」⇒ 建筑轮廓是这三档的必需数据。
            // 只有真的要贴边线时才抓（市辖区档用不到，省一次全城物体树遍历）。
            if (includeObjects) CollectObjects(world, corridor, lk);

            world.BuildAdjacency();
            world.DetectRings();

            // 第八轮反馈 3 的机器凭据：这一份快照里有多少东西**因为是地下的**被请出贴合目标。
            // 边那一侧仍然进快照（引擎层的 PolicyKit.IsTargetNet 是第二道闸，两处口径必须一致），
            // 所以这里是"数出来"的而不是"扔掉的"；建筑那一侧在采样阶段就不进快照（见 CollectObjects），
            // 由 m_lastUndergroundObjects 带过来。合起来赋值给 SnapperState.UndergroundDropped：
            // 实机统计行的 under= 要能看出「玩家那条隧道到底有没有被抓进来又被排除」，
            // 而不是只看一个永远为 0 的假绿。
            int under = 0;
            for (int i = 0; i < world.Edges.Count; i++)
            {
                GraphEdge ge = world.Edges[i];
                if (ge != null && ge.Underground) under++;
            }
            SnapperState.UndergroundDropped = under + m_lastUndergroundObjects;
            return world;
        }

        // ————————————————————————————————— 网络

        private void CollectNetEntities(Box2 corridor, NativeList<Entity> edges, NativeList<Entity> nodes)
        {
            // FACT：Game.Net.SearchSystem.GetNetSearchTree(bool readOnly, out JobHandle) 在 SearchSystem.cs:829，
            // 用法范式见 AreaToolSystem.cs:1253-1269（游戏自己贴道路时就是这个调用）。
            SearchSystem search = World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<SearchSystem>();
            JobHandle deps;
            NativeQuadTree<Entity, QuadTreeBoundsXZ> tree = search.GetNetSearchTree(true, out deps);
            deps.Complete();
            search.AddNetSearchTreeReader(deps);

            NetGather gather = new NetGather
            {
                m_Edges = edges,
                m_Nodes = nodes,
                m_Manager = m_em,
                m_Bounds = ToBounds3(corridor)
            };
            tree.Iterate(ref gather);
        }

        /// <summary>
        /// 四叉树迭代器。必须【同时】实现 INativeQuadTreeIterator 与 IUnsafeQuadTreeIterator，
        /// 只实现后者会 CS0315（Playbook 步骤 3.4 明记的坑）。不 Burst：只在玩家画区域时按需跑一次。
        ///
        /// ⚠ <c>Intersect</c> 不是「顺手写个 true」的装饰：<c>NativeQuadTree.Iterate</c> 的唯一重载是
        /// <c>Iterate&lt;TIterator&gt;(ref TIterator, int startNode = 0)</c> —— **没有**按包围盒查询的重载，
        /// 遍历范围完全由这个回调决定（FACT：ilspycmd -t Colossal.Collections.NativeQuadTree`2 的成员清单；
        /// 游戏自己的 SnapJob 正是在 Intersect 里 <c>MathUtils.Intersect(bounds.m_Bounds, m_Bounds)</c>）。
        /// 老实现这里返回 true，于是「抓一条走廊」实际把**整张全城网络树**走了一遍，
        /// 每个条目两次 HasComponent —— 大城市里这就是每次放点的卡顿。现在真正按盒子裁。
        /// </summary>
        private struct NetGather : INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>, IUnsafeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
        {
            public NativeList<Entity> m_Edges;
            public NativeList<Entity> m_Nodes;
            public EntityManager m_Manager;
            public Bounds3 m_Bounds;

            public bool Intersect(QuadTreeBoundsXZ b)
            {
                return MathUtils.Intersect(b.m_Bounds, m_Bounds);
            }

            public void Iterate(QuadTreeBoundsXZ b, Entity item)
            {
                if (item == Entity.Null) return;
                if (!MathUtils.Intersect(b.m_Bounds, m_Bounds)) return;
                // 四叉树里既有边也有节点条目，靠组件区分（两者共用同一棵树）。
                if (m_Manager.HasComponent<Game.Net.Edge>(item))
                {
                    if (!m_Edges.Contains(item)) m_Edges.Add(item);
                }
                else if (m_Manager.HasComponent<Game.Net.Node>(item))
                {
                    if (!m_Nodes.Contains(item)) m_Nodes.Add(item);
                }
            }
        }

        private void BuildNodesAndEdges(WorldSnapshot world, NativeList<Entity> nodeEntities, NativeList<Entity> edgeEntities, SamplerLookups lk)
        {
            Dictionary<Entity, int> nodeIndex = new Dictionary<Entity, int>();
            for (int i = 0; i < nodeEntities.Length; i++)
            {
                Entity e = nodeEntities[i];
                if (!lk.Nodes.TryGetComponent(e, out Game.Net.Node netNode)) continue;
                nodeIndex[e] = world.Nodes.Count;
                world.Nodes.Add(new GraphNode
                {
                    Id = world.Nodes.Count,
                    Pos = ToP3(netNode.m_Position),
                    Degree = 0,
                    Signature = SignatureOf(e, netNode.m_Position)
                });
            }

            for (int i = 0; i < edgeEntities.Length; i++)
            {
                Entity e = edgeEntities[i];
                if (!lk.Edges.TryGetComponent(e, out Game.Net.Edge edge)) continue;
                if (!lk.Curves.TryGetComponent(e, out Game.Net.Curve curve)) continue;
                if (!nodeIndex.TryGetValue(edge.m_Start, out int startIdx)) startIdx = -1;
                if (!nodeIndex.TryGetValue(edge.m_End, out int endIdx)) endIdx = -1;
                // 端点在走廊外 ⇒ 这条边不参与描边（否则图会通向没抓到的节点，Dijkstra 会走丢）。
                if (startIdx < 0 || endIdx < 0) continue;

                Polyline line = SampleBezier(curve.m_Bezier, curve.m_Length);
                if (line.Count < 2) continue;

                GraphEdge ge = new GraphEdge
                {
                    Id = world.Edges.Count,
                    Kind = ClassifyNet(e),
                    StartNode = startIdx,
                    EndNode = endIdx,
                    Line = line,
                    Length = Math.Max(curve.m_Length, line.Length2D()),
                    Signature = SignatureOf(e, curve.m_Bezier.a)
                };
                // 需求 3：地块/表面类要贴**路缘**；需求 2：弯道节点密度要知道**路面宽度**；
                // 需求 5：高架也要能贴 ⇒ 把高架位一并带出来（判据只看 XZ，高度不参与）。
                FillEdgeGeometry(ge, e, line, lk);
                // 需求 8：环岛/环线。FACT：Game.Net.Roundabout{float m_Radius}（Roundabout.cs:8）。
                if (m_em.HasComponent<Game.Net.Roundabout>(e)) ge.OnRing = true;
                world.Edges.Add(ge);
            }
        }

        /// <summary>
        /// 一条边的外沿折线 + prefab 元数据。任何一步读不到都**只缺这一档**：
        /// 边线为 null ⇒ 该类别在这一条路上退回中心线贴合；宽度 ≤0 ⇒ 密度按玩家明确要的最小路宽 3 米兜底。
        /// 绝不让整次走廊抓取失败（老实现把这个逻辑写在 <c>SidePolyline</c> 的 try 里，出问题时只有 null、没有原因）。
        /// </summary>
        private void FillEdgeGeometry(GraphEdge ge, Entity e, Polyline centreLine, SamplerLookups lk)
        {
            double leftHalf = 0, rightHalf = 0;
            double elevatedHeight = 0;
            if (lk.PrefabRefs.TryGetComponent(e, out Game.Prefabs.PrefabRef pr))
            {
                if (lk.PrefabNetGeometry.TryGetComponent(pr.m_Prefab, out Game.Prefabs.NetGeometryData ngd))
                {
                    ge.LayerBits = (uint)ngd.m_MergeLayers;
                    // ⚠ 这里原来写的是 <c>ngd.m_ElevatedWidth</c> —— 那是**高架变体的路面宽度**
                    //（FACT：Game.Prefabs/NetGeometryData.cs 里 m_ElevatedWidth 与 m_DefaultWidth 并排，
                    //  两个都是 float 宽度；高度是另一对 m_DefaultHeightRange / m_ElevatedHeightRange，
                    //  类型是 Bounds1）。把一个宽度当高度用，结果就是「双向六车道的高架 Lift=24 米」，
                    //  而 TraceKit.HeightChanged 拿 Lift 判高度切换 ⇒ 平地上两条不同宽度的路也被判成"高度变了"，
                    //  白白多钉一个节点。第六轮反馈 8 要求按高度定节点位置，这个源头必须先修对。
                    if (ngd.m_ElevatedHeightRange.max > 0f) elevatedHeight = ngd.m_ElevatedHeightRange.max;
                    // —— 反馈 9 的白名单靠 prefab 上这两位分辨「埠头」与「归某个建筑所有的内部路」。
                    // FACT：Game.Net/GeometryFlags.cs —— SubOwner = 0x1000000、OnWater = 0x2000000；
                    // 游戏自己判「这条 net 归别人所有」用的就是 SubOwner + Owner 一起查
                    //（Game.Pathfind/LaneDataSystem.cs:525、ParkingLaneDataSystem.cs:399）。
                    if ((ngd.m_Flags & Game.Net.GeometryFlags.OnWater) != 0) ge.OnWater = true;
                    if ((ngd.m_Flags & Game.Net.GeometryFlags.SubOwner) != 0) ge.Internal = true;
                }
            }
            // SubOwner 位没打上、但实体确实挂在某栋建筑名下的（园区步道一类的脏数据形状）：
            // 再按 Owner 链兜一次。两条判据任一成立就算内部路，判不出来时**不算**（fail-open，
            // 宁可多贴一条路，也不能因为一条不确定的证据把整片路网从白名单里踢掉）。
            if (!ge.Internal && IsBuildingOwned(e, lk)) ge.Internal = true;
            if (lk.Compositions.TryGetComponent(e, out Game.Net.Composition comp))
            {
                if (lk.PrefabNetComposition.TryGetComponent(comp.m_Edge, out Game.Prefabs.NetCompositionData ncd))
                {
                    double w = ncd.m_Width > 0f ? ncd.m_Width : 0;
                    double mid = ncd.m_MiddleOffset;
                    // 反馈 3 的关键一格：外沿 = 人行道外缘。游戏自己算两侧边线用的就是
                    // m_Width*(0.5,-0.5)+m_MiddleOffset（FACT：GeometrySystem.cs:526-530），
                    // 其中 m_Width 已含人行道（BlockSystem.cs:195 拿 m_Width-m_MiddleOffset*2 当建筑可占地宽度起点）。
                    // 只取 m_Width/2 会漏掉不对称量：单边人行道 / 中央分隔带 / 隔音屏的路，
                    // 内侧那条缘线就整体偏 m_MiddleOffset 米 ⇒ 玩家说「这不是人行道边」。
                    //
                    // 这道翻法本身在 Engine 层（GraphEdge.SetWidthsFromPrefab）：它是纯算术，
                    // 写在采样器里就只有开游戏才看得见对错，现在回归壳 W 段能直接钉住。
                    // 宽度读不到（0）时**不调**：那等于「这条路没数据」，不许把别处已经填好的宽度清零。
                    if (w > 0) ge.SetWidthsFromPrefab(w, mid);
                    leftHalf = ge.LeftHalfWidth;
                    rightHalf = ge.RightHalfWidth;
                    ge.Elevated = (ncd.m_Flags.m_General & Game.Prefabs.CompositionFlags.General.Elevated) != 0;
                    ge.Tunnel = (ncd.m_Flags.m_General & Game.Prefabs.CompositionFlags.General.Tunnel) != 0;
                    if (ncd.m_HeightRange.max > 0f) ge.Lift = Math.Max(ge.Lift, ncd.m_HeightRange.max);
                }
            }
            if (lk.Elevations.TryGetComponent(e, out Game.Net.Elevation el))
            {
                ge.Lift = Math.Max(ge.Lift, Math.Max(el.m_Elevation.x, el.m_Elevation.y));
                // 第八轮反馈 3：「建在地下的所有物体」都不许当目标。游戏自己判地下用的就是这个值
                // （FACT：Game.Net/Elevation.cs:8-10 的 m_Elevation 两端高程；NetUtils.cs:594 把
                //  Tunnel 组合映射到 CollisionMask.Underground、Elevated 映射到 Overground；
                //  NetToolSystem.cs:6078/6240 的 requireUnderground 用「< 0 且低于 -3×限差，或 LoweredIsTunnel」）。
                // 我们不照抄那个阈值（那是"这一格能不能连"的判据），只把**最低的那一头**存进 GraphEdge，
                // 由引擎层的 Underground 属性统一给一个 -0.5 米的界（下沉式立交/明挖段的地面就在路面以下）。
                ge.LowElevation = Math.Min(ge.LowElevation, Math.Min(el.m_Elevation.x, el.m_Elevation.y));
            }
            // 高架那一档用 prefab 的**高度区间**，且只在它真的是高架组合时才用 ——
            // 给地面路也背上"高架高度"的话，TraceKit.HeightChanged 又会在平地上判出高度切换。
            if (ge.Elevated && elevatedHeight > 0) ge.Lift = Math.Max(ge.Lift, elevatedHeight);

            // —— 真·左右路缘。
            // FACT：AreaToolSystem.cs:452-460 的 Snap.NetSide 就是拿 EdgeGeometry 的
            // m_Start.m_Left / m_Start.m_Right / m_End.m_Left / m_End.m_Right 四条贝塞尔去吸的：
            // 每条边线的**上半段**在 m_Start 里、**下半段**在 m_End 里（GeometrySystem.cs:398/405
            // 分别用 Cut(leftStartCurve, leftEndCurve, …) 与 Cut(leftEndCurve, leftStartCurve, …) 写进去）。
            // 所以下面是「同侧两段拼一条」，不是「任取一段」。
            if (lk.EdgeGeoms.TryGetComponent(e, out Game.Net.EdgeGeometry eg))
            {
                ge.LeftLine = StitchEdge(eg.m_Start.m_Left, eg.m_End.m_Left, centreLine, leftHalf, true);
                ge.RightLine = StitchEdge(eg.m_Start.m_Right, eg.m_End.m_Right, centreLine, rightHalf, false);
            }
            else
            {
                ge.LeftLine = OffsetCopy(centreLine, leftHalf, true);
                ge.RightLine = OffsetCopy(centreLine, rightHalf, false);
            }
        }

        /// <summary>
        /// 这条 net 是不是归某栋建筑所有（园区/厂区内部的专用道、停车场的车道）。
        /// 走法与游戏自己一致：沿 <c>Game.Common.Owner</c> 往上走，直到那一格带
        /// <c>Game.Buildings.Building</c>（FACT：Game.Net/ReferencesSystem.cs:701-705 的
        /// <c>AllowConnection</c> 就是 <c>while (Owner 存在 &amp;&amp; 还不是建筑) owner = Owner.m_Owner;</c>）。
        /// 链长上限 8 与 <see cref="RootBuildingIndex"/> 同口径：纯防脏数据成环把主线程挂住。
        /// 没有 Owner（玩家自己修的路就是这种）⇒ false。
        /// </summary>
        private static bool IsBuildingOwned(Entity e, SamplerLookups lk)
        {
            Entity cur = e;
            for (int hop = 0; hop < 8; hop++)
            {
                if (cur == Entity.Null) return false;
                Game.Common.Owner own;
                if (!lk.Owners.TryGetComponent(cur, out own)) return false;
                if (own.m_Owner == cur || own.m_Owner == Entity.Null) return false;
                cur = own.m_Owner;
                if (lk.BuildingFlags.HasComponent(cur)) return true;
            }
            return false;
        }

        /// <summary>没有边线数据时的兜底：中心线按该侧半幅宽平移（左右各自按自己的不对称宽度）。</summary>
        private Polyline OffsetCopy(Polyline centreLine, double halfWidth, bool left)        {
            Polyline outLine = new Polyline();
            if (centreLine == null || centreLine.Count < 2 || !(halfWidth > 0)) return outLine;
            return OffsetLine(centreLine, halfWidth, left);
        }

        /// <summary>边线太短（只到路口那一小截）时，认定它不是整条边的外沿，改按宽度平移中心线。</summary>
        private const double EDGE_COVERAGE = 0.6;

        /// <summary>
        /// 把同侧的两段贝塞尔拼成一条外沿折线；拼不出来就按「中心线 ± 半幅路宽」偏移兜底。
        /// </summary>
        private Polyline StitchEdge(Bezier4x3 a, Bezier4x3 b, Polyline centreLine, double halfWidth, bool left)
        {
            Polyline pa = SampleBezier(a, 0f, EDGE_SAMPLE_STEP);
            Polyline pb = SampleBezier(b, 0f, EDGE_SAMPLE_STEP);
            if (pa.Count >= 2 && pb.Count >= 2)
            {
                Polyline joined = Join(pa, pb);
                double refLen = centreLine != null ? centreLine.Length2D() : 0;
                if (refLen <= 0 || joined.Length2D() >= refLen * EDGE_COVERAGE)
                {
                    // 统一成「起点 = 边的 StartNode 那一头」。弧长是这条折线自己的量，
                    // 方向反了会让描边从**另一头**整条拖进来（v0.1.0 的 T6 回归就是这个形状）。
                    OrientTo(joined, centreLine);
                    return joined;
                }
            }
            if (halfWidth > 0) return OffsetLine(centreLine, halfWidth, left);
            return null;
        }

        /// <summary>把折线翻到与 <paramref name="reference"/> 同向（首点贴 reference 首点）。</summary>
        private static void OrientTo(Polyline line, Polyline reference)
        {
            if (line == null || line.Count < 2 || reference == null || reference.Count < 2) return;
            double head = line.Points[0].DistanceTo(reference.Points[0]);
            double tail = line.Points[line.Count - 1].DistanceTo(reference.Points[0]);
            if (tail < head) line.Points.Reverse();
        }

        /// <summary>
        /// 两段相接。哪一头对哪一头**不写死**：EdgeGeometry 里 m_End 那一段的方向在不同 prefab 上
        /// 不一致（GeometrySystem.cs:388/391 对 m_End.m_Left 连做了两次 <c>MathUtils.Invert</c>），
        /// 所以这里按「四个端点两两距离最小」判方向，两种可能里取接缝更近的那一种。
        /// </summary>
        private static Polyline Join(Polyline a, Polyline b)
        {
            double straight = a.Points[a.Count - 1].DistanceTo(b.Points[0]);
            double flipped = a.Points[a.Count - 1].DistanceTo(b.Points[b.Count - 1]);
            Polyline outLine = new Polyline();
            outLine.AddRange(a.Points);
            if (flipped < straight)
            {
                for (int i = b.Count - 1; i >= 1; i--) outLine.Add(b.Points[i]);
            }
            else
            {
                for (int i = 1; i < b.Count; i++) outLine.Add(b.Points[i]);
            }
            return outLine;
        }

        /// <summary>
        /// 中心线沿法向平移 halfWidth。法向取「相邻两段的平均方向」再转 90°，
        /// 拐角处因此是**角平分线**偏移而不是两段各自的法向 —— 不这么做，路口会鼓包。
        /// 偏移量比真实路缘小一点是正常的：这条线只在 prefab 没给边线时兜底，
        /// 有边线时走 <see cref="StitchEdge"/> 的真数据。
        /// </summary>
        private static Polyline OffsetLine(Polyline centreLine, double halfWidth, bool left)
        {
            Polyline outLine = new Polyline();
            if (centreLine == null || centreLine.Count < 2 || !(halfWidth > 0)) return outLine;
            int n = centreLine.Count;
            for (int i = 0; i < n; i++)
            {
                Engine.P3 p = centreLine.Points[i];
                Engine.P3 prev = centreLine.Points[i > 0 ? i - 1 : i];
                Engine.P3 next = centreLine.Points[i < n - 1 ? i + 1 : i];
                double dx = next.X - prev.X;
                double dy = next.Y - prev.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len <= 1e-9) { outLine.Add(p); continue; }
                dx /= len;
                dy /= len;
                // 左手定则与右手定则二选一，靠 left 参数固定：同一条边两次调用只有这一个变量不同，
                // 于是两条偏移线必然分居中心线两侧（需求 3 的「跨越」判据依赖这一点）。
                double nx = left ? -dy : dy;
                double ny = left ? dx : -dx;
                outLine.Add(new Engine.P3(p.X + nx * halfWidth, p.Y + ny * halfWidth, p.H));
            }
            return outLine;
        }

        /// <summary>网络类型判定。FACT：Game.Net 下 Road/TrainTrack/TramTrack/SubwayTrack/Waterway/PedestrianLane 都是空标记组件。</summary>
        private NetKind ClassifyNet(Entity e)
        {
            if (m_em.HasComponent<Game.Net.TrainTrack>(e)) return NetKind.Rail;
            if (m_em.HasComponent<Game.Net.TramTrack>(e)) return NetKind.Rail;
            if (m_em.HasComponent<Game.Net.SubwayTrack>(e)) return NetKind.Rail;
            if (m_em.HasComponent<Game.Net.Waterway>(e)) return NetKind.Waterway;
            if (m_em.HasComponent<Game.Net.PedestrianLane>(e)) return NetKind.Path;
            if (m_em.HasComponent<Game.Net.Road>(e)) return NetKind.Road;
            return NetKind.Other;
        }

        private Polyline SampleBezier(Bezier4x3 bezier, float length)
        {
            return SampleBezier(bezier, length, SAMPLE_STEP);
        }

        private Polyline SampleBezier(Bezier4x3 bezier, float length, double step)
        {
            Polyline line = new Polyline();
            double len = length > 0f ? length : MathUtils.Length(bezier);
            if (!(step > 0)) step = SAMPLE_STEP;
            int n = (int)Math.Ceiling(len / step);
            if (n < 2) n = 2;
            if (n > MAX_SAMPLES_PER_EDGE) n = MAX_SAMPLES_PER_EDGE;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / (n - 1);
                line.Add(ToP3(MathUtils.Position(bezier, t)));
            }
            return line;
        }

        // 这里原本是 SidePolyline()：用 (m_Start.m_Left.a + m_Start.m_Right.a)/2 当「路缘偏移」。
        // 那个中点就是**中心线**本身，减掉中心线起点得到的偏移量恒接近 0 ⇒ 产出的 SideLine ≈ Line，
        // 于是 v0.1.0/v0.1.1 的「贴路缘」一直在贴中心线，玩家要的「产业区/地皮贴道路边缘」做不到。
        // 现在由 FillEdgeGeometry 按游戏自己的四条边线曲线重建（见 EDGE_SAMPLE_STEP 处注释）。

        // ————————————————————————————————— 建筑轮廓（需求 3 的「同一建筑边缘」档）

        /// <summary>
        /// 抓走廊内的**建筑**落地轮廓（第六轮反馈 9 之后：只抓建筑，摆件/树/桥墩不进快照）。
        /// 轮廓的算法与游戏自己的 <c>Snap.ObjectSide</c> 一致：
        /// prefab 上有 <c>BuildingData</c> 时把 bounds.xz 换成 <c>m_LotSize * ±4</c>，
        /// 再 <c>ObjectUtils.CalculateBaseCorners(transform.m_Position, transform.m_Rotation, bounds)</c>
        /// 取 Quad3 的四条边（FACT：AreaToolSystem.cs:651-663）。
        /// **收哪些物体**则比游戏严：游戏那一档对任何非圆形静态物体都贴边，
        /// 我们只认建筑（判据见 <see cref="BuildObjectOutline"/>）。
        /// 圆形 prefab（<c>GeometryFlags.Circular</c>）游戏自己不贴边、我们也不贴：
        /// 拿一个外接方框当轮廓会把边界画到树冠外面去。
        /// </summary>
        private void CollectObjects(WorldSnapshot world, Box2 corridor, SamplerLookups lk)
        {
            NativeList<Entity> found = new NativeList<Entity>(Allocator.TempJob);
            try
            {
                // FACT：Game.Objects.SearchSystem.GetStaticSearchTree(bool, out JobHandle)（Objects/SearchSystem.cs:419）
                //       + AddStaticSearchTreeReader（同文件 :432）。移动物体（车辆）不参与，只抓静态。
                Game.Objects.SearchSystem objects =
                    World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<Game.Objects.SearchSystem>();
                JobHandle deps;
                NativeQuadTree<Entity, QuadTreeBoundsXZ> tree = objects.GetStaticSearchTree(true, out deps);
                deps.Complete();
                objects.AddStaticSearchTreeReader(deps);

                ObjectGather gather = new ObjectGather
                {
                    m_Result = found,
                    m_Manager = m_em,
                    m_Bounds = ToBounds3(corridor),
                    m_Limit = OBJECT_LIMIT
                };
                tree.Iterate(ref gather);

                int notBuilding = 0;
                int noOutline = 0;
                int underground = 0;
                for (int i = 0; i < found.Length; i++)
                {
                    bool isBuilding;
                    float baseY;
                    Polyline outline = BuildObjectOutline(found[i], lk, out isBuilding, out baseY);
                    if (!isBuilding)
                    {
                        // 反馈 9 的白名单在这里落地：树、摆件、高架桥墩、码头桩这些**不是建筑**的静态物体
                        // 直接不进快照。留在快照里再由引擎过滤也能得到同样的结果，但那样会白占
                        // OBJECT_LIMIT 的名额 —— 一片树林就能把整条街的真建筑挤出去。
                        notBuilding++;
                        continue;
                    }
                    if (outline == null) { noOutline++; continue; }
                    // 第八轮反馈 3：地下物体（地下的楼、盖起来的隧道房）一律不是吸附目标。
                    // 和摆件同一处理：在采样阶段就不进快照，别让它白占 OBJECT_LIMIT 的名额。
                    // 判据用 transform.m_Position.y（FACT：Game.Objects/Transform 的 m_Position 就是物体落点，
                    // ObjectUtils.cs:262-286 算轮廓时只取它的 xz 与 y），阈值与道路那侧一致：-0.5 m。
                    if (baseY < -0.5f)
                    {
                        underground++;
                        continue;
                    }
                    ObjectRef or = new ObjectRef
                    {
                        Id = world.Objects.Count,
                        OwnerIndex = RootBuildingIndex(found[i], lk),
                        Line = outline,
                        Signature = SignatureOf(found[i], ToFloat3(outline.Points[0])),
                        IsBuilding = true,
                        Underground = false
                    };
                    world.Objects.Add(or);
                }
                // 实机验收要看这一行：它在，才说明白名单真的把摆件挡在了外面（而不是"代码没跑到"）。
                m_lastUndergroundObjects = underground;
                if ((notBuilding > 0 || underground > 0) && !m_nonBuildingLogged)
                {
                    m_nonBuildingLogged = true;
                    SnapperLog.Info("[ZoneSnapper] 建筑轮廓白名单已生效：这一片 " + notBuilding +
                                    " 个静态物体不是建筑（树 / 摆件 / 桥墩之类）、" + underground +
                                    " 个在地下（第八轮反馈 3），均已排除在贴合目标之外；另有 " +
                                    noOutline + " 个建筑拿不到轮廓（第六轮反馈 9）");
                }
            }
            catch (Exception e)
            {
                SnapperLog.Warn("抓建筑轮廓失败（这一档跳过）：" + e.GetType().Name);
            }
            finally
            {
                found.Dispose();
            }
        }

        /// <summary>
        /// 一栋建筑的落地轮廓。<paramref name="isBuilding"/> 交出「它算不算建筑」——
        /// 这是反馈 9 的白名单判据，与「轮廓画不画得出来」是两件事，所以分开交：
        ///  · 判据：prefab 上有 <c>Game.Prefabs.BuildingData</c>，或 <c>ObjectGeometryData.m_Flags</c>
        ///    带 <c>Game.Objects.GeometryFlags.HasLot</c>（= 0x100000，"这物体占一块地"）
        ///    （FACT：research/decompiled/Game.Objects/GeometryFlags.cs:28）。
        ///  · 游戏自己那一档对**任何**非圆形静态物体都贴边，<c>BuildingData</c> 只用来把包围盒换成地块尺寸
        ///    （FACT：AreaToolSystem.cs:651-663）⇒ 原版会把地块角点吸到一棵树的外接框上。这一层是我们加的。
        ///
        /// 轮廓本身的口径仍与游戏一致：<c>ObjectUtils.CalculateBaseCorners(transform.m_Position,
        /// transform.m_Rotation, bounds)</c> 出 Quad3，取四条边。圆形 prefab
        /// （<c>GeometryFlags.Circular</c>）游戏自己不贴边、我们也不贴：拿外接方框当轮廓会把边界画到树冠外面去。
        /// </summary>
        private Polyline BuildObjectOutline(Entity instance, SamplerLookups lk, out bool isBuilding, out float baseY)
        {
            isBuilding = false;
            baseY = 0f;
            if (!lk.PrefabRefs.TryGetComponent(instance, out Game.Prefabs.PrefabRef prefabRef)) return null;
            if (!lk.ObjectTransforms.TryGetComponent(instance, out Game.Objects.Transform xf)) return null;
            if (!lk.PrefabObjectGeometry.TryGetComponent(prefabRef.m_Prefab, out Game.Prefabs.ObjectGeometryData geom)) return null;
            if ((geom.m_Flags & Game.Objects.GeometryFlags.Circular) != 0) return null;

            bool hasBuildingData = lk.PrefabBuildingData.TryGetComponent(prefabRef.m_Prefab, out Game.Prefabs.BuildingData bd);
            if (!hasBuildingData && (geom.m_Flags & Game.Objects.GeometryFlags.HasLot) == 0) return null;   // 不是建筑 ⇒ 不进白名单
            isBuilding = true;

            Bounds3 bounds = geom.m_Bounds;
            if (hasBuildingData)
            {
                // 与游戏同一行代码的口径：地块类建筑按地块尺寸取轮廓，而不是按网格包围盒。
                bounds.min.xz = new float2(bd.m_LotSize.x, bd.m_LotSize.y) * -4f;
                bounds.max.xz = new float2(bd.m_LotSize.x, bd.m_LotSize.y) * 4f;
            }
            if (bounds.max.x <= bounds.min.x || bounds.max.z <= bounds.min.z) return null;

            Quad3 quad = Game.Objects.ObjectUtils.CalculateBaseCorners(xf.m_Position, xf.m_Rotation, bounds);
            // 第八轮反馈 3：这栋楼的落地高度。游戏自己判「在地下」用的就是它
            //（ObjectUtils.cs:262-286：elevation.m_Elevation < 0f ⇒ CollisionMask.Underground）。
            baseY = xf.m_Position.y;
            Polyline line = new Polyline();
            line.Add(ToP3(quad.a));
            line.Add(ToP3(quad.b));
            line.Add(ToP3(quad.c));
            line.Add(ToP3(quad.d));
            line.Add(ToP3(quad.a));          // 闭合：贴任意一条边都能沿轮廓走完一圈
            return line;
        }

        /// <summary>
        /// 沿 <c>Game.Common.Owner</c> 链走到带 <c>Game.Buildings.Building</c> 的那一格 ——
        /// 这就是「同一栋建筑」的判据（主楼和它的附属体共用一个根）。
        /// FACT：同一套走法见 Game.Net/ReferencesSystem.cs:701-705（它是 while + Owner 直到有 BuildingData）。
        /// 链长上限 8：游戏里的嵌套没这么深，纯粹是防脏数据成环把主线程挂住。
        /// </summary>
        private int RootBuildingIndex(Entity instance, SamplerLookups lk)
        {
            Entity cur = instance;
            for (int hop = 0; hop < 8; hop++)
            {
                if (cur == Entity.Null) break;
                if (lk.BuildingFlags.HasComponent(cur)) return cur.Index;
                Game.Common.Owner owner;
                if (!lk.Owners.TryGetComponent(cur, out owner)) break;
                if (owner.m_Owner == cur || owner.m_Owner == Entity.Null) break;
                cur = owner.m_Owner;
            }
            return instance.Index;
        }

        /// <summary>四叉树迭代器：只收「在这个包围盒里、且不是预览态」的物体（裁剪口径同 <see cref="NetGather"/>）。</summary>
        private struct ObjectGather : INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>, IUnsafeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
        {
            public NativeList<Entity> m_Result;
            public Bounds3 m_Bounds;
            public EntityManager m_Manager;
            public int m_Limit;

            public bool Intersect(QuadTreeBoundsXZ b)
            {
                return MathUtils.Intersect(b.m_Bounds, m_Bounds);
            }

            public void Iterate(QuadTreeBoundsXZ b, Entity item)
            {
                if (item == Entity.Null) return;
                if (m_Limit > 0 && m_Result.Length >= m_Limit) return;
                if (!MathUtils.Intersect(b.m_Bounds, m_Bounds)) return;
                if (m_Manager.HasComponent<Game.Tools.Temp>(item)) return;   // 预览态的假建筑不算轮廓
                if (!m_Result.Contains(item)) m_Result.Add(item);
            }
        }

        // ————————————————————————————————— 已有区域边界

        /// <summary>
        /// 盒子里已有的区域实体（供「跟随已提交区域」挑候选）。返回的是普通 List，调用方在主线程用。
        /// </summary>
        public List<Entity> AreaEntities(Box2 corridor, int limit)
        {
            List<Entity> list = new List<Entity>();
            NativeList<Entity> found = new NativeList<Entity>(Allocator.TempJob);
            try
            {
                CollectAreaEntities(corridor, found, limit);
                for (int i = 0; i < found.Length; i++) list.Add(found[i]);
            }
            catch (Exception e)
            {
                SnapperLog.Warn("收集区域实体失败：" + e.GetType().Name);
            }
            finally
            {
                found.Dispose();
            }
            return list;
        }

        private void CollectAreaEntities(Box2 corridor, NativeList<Entity> found, int limit)
        {
            // FACT：Game.Areas.SearchSystem.GetSearchTree(bool, out JobHandle)（Areas/SearchSystem dump :281）
            //       + AddSearchTreeReader（同 dump :294），用法范式 AreaToolSystem.cs:3780 / :1238。
            Game.Areas.SearchSystem areas = World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<Game.Areas.SearchSystem>();
            JobHandle deps;
            NativeQuadTree<Game.Areas.AreaSearchItem, QuadTreeBoundsXZ> tree = areas.GetSearchTree(true, out deps);
            deps.Complete();
            areas.AddSearchTreeReader(deps);

            AreaGather gather = new AreaGather
            {
                m_Result = found,
                m_Manager = m_em,
                m_Bounds = ToBounds3(corridor),
                m_Limit = limit
            };
            tree.Iterate(ref gather);
        }

        private void CollectAreaBorders(WorldSnapshot world, Box2 corridor, SamplerLookups lk)
        {
            NativeList<Entity> found = new NativeList<Entity>(Allocator.TempJob);
            try
            {
                CollectAreaEntities(corridor, found, 0);

                for (int i = 0; i < found.Length; i++)
                {
                    BorderRef br = BuildBorder(found[i], corridor, lk);
                    if (br == null) continue;
                    if (br.Tier == AreaTier.MapTile) world.MapTiles.Add(br);
                    else world.Borders.Add(br);
                }
            }
            catch (Exception e)
            {
                SnapperLog.Warn("抓已有区域边界失败（这一档跳过）：" + e.GetType().Name);
            }
            finally
            {
                found.Dispose();
            }
        }

        private BorderRef BuildBorder(Entity areaEntity, Box2 corridor, SamplerLookups lk)
        {
            if (!m_em.HasComponent<Game.Areas.Area>(areaEntity)) return null;
            if (m_em.HasComponent<Game.Tools.Temp>(areaEntity)) return null;   // 预览态的临时区域不算边界
            if (!lk.AreaNodes.TryGetBuffer(areaEntity, out DynamicBuffer<Game.Areas.Node> nodes)) return null;
            if (nodes.Length < 3) return null;

            AreaTier tier = AreaTier.Other;
            if (m_em.HasComponent<Game.Areas.District>(areaEntity)) tier = AreaTier.District;
            else if (m_em.HasComponent<Game.Areas.Lot>(areaEntity)) tier = AreaTier.Lot;
            else if (m_em.HasComponent<Game.Areas.Surface>(areaEntity)) tier = AreaTier.Surface;
            else if (m_em.HasComponent<Game.Areas.Space>(areaEntity)) tier = AreaTier.Space;
            else if (m_em.HasComponent<Game.Areas.MapTile>(areaEntity)) tier = AreaTier.MapTile;

            Polyline line = new Polyline();
            bool inside = false;
            for (int i = 0; i < nodes.Length; i++)
            {
                // 游戏 Node.m_Position 是 (x=世界X, y=高度, z=世界Z)，引擎 P3 是 (X, Y=世界Z, H=高度)。
                Engine.P3 p = new Engine.P3(nodes[i].m_Position.x, nodes[i].m_Position.z, nodes[i].m_Position.y);
                line.Add(p);
                if (corridor.Contains(p)) inside = true;
            }
            if (!inside) return null;
            // 显式闭合：游戏自己的区域节点是闭合环（FACT：生成瓦片时 dynamicBuffer[4] = dynamicBuffer[0]，ATS:1499），
            // 但并非所有区域都重复首点；不闭合会在收尾处断一截。
            if (line.Count >= 3 && line.Points[line.Count - 1].DistanceTo(line.Points[0]) > 0.01)
            {
                line.Add(line.Points[0]);
            }
            unchecked
            {
                long sig = 1469598103934665603L;
                for (int i = 0; i < line.Count; i++)
                {
                    sig = (sig ^ (long)line.Points[i].X) * 1099511628211L;
                    sig = (sig ^ (long)line.Points[i].Y) * 1099511628211L;
                }
                return new BorderRef { AreaId = areaEntity.Index, Tier = tier, Line = line, Signature = sig };
            }
        }

        private struct AreaGather : INativeQuadTreeIterator<Game.Areas.AreaSearchItem, QuadTreeBoundsXZ>, IUnsafeQuadTreeIterator<Game.Areas.AreaSearchItem, QuadTreeBoundsXZ>
        {
            public NativeList<Entity> m_Result;
            public EntityManager m_Manager;
            public Bounds3 m_Bounds;
            public int m_Limit;

            /// <summary>
            /// 区域树同样没有按盒子查询的重载，裁不裁全看这里（口径与 <see cref="NetGather"/> 同一条注释）。
            /// 一棵树上的条目是「区域的某个三角形」，一个区域会有多条目 —— 结果表按实体去重。
            /// </summary>
            public bool Intersect(QuadTreeBoundsXZ b)
            {
                return MathUtils.Intersect(b.m_Bounds, m_Bounds);
            }

            public void Iterate(QuadTreeBoundsXZ b, Game.Areas.AreaSearchItem item)
            {
                if (m_Limit > 0 && m_Result.Length >= m_Limit) return;
                Entity e = item.m_Area;
                if (e == Entity.Null) return;
                if (!MathUtils.Intersect(b.m_Bounds, m_Bounds)) return;
                if (!m_Result.Contains(e)) m_Result.Add(e);
            }
        }

        // ————————————————————————————————— 坐标换算

        public static Engine.P3 ToP3(float3 v)
        {
            return new Engine.P3(v.x, v.z, v.y);
        }

        public static float3 ToFloat3(Engine.P3 p)
        {
            return new float3((float)p.X, (float)p.H, (float)p.Y);
        }

        private static Bounds3 ToBounds3(Box2 box)
        {
            return new Bounds3(
                new float3((float)box.MinX, -10000f, (float)box.MinY),
                new float3((float)box.MaxX, 10000f, (float)box.MaxY));
        }

        private static long SignatureOf(Entity e, float3 reference)
        {
            unchecked
            {
                long h = 1469598103934665603L;
                h = (h ^ e.Index) * 1099511628211L;
                h = (h ^ e.Version) * 1099511628211L;
                h = (h ^ (long)reference.x) * 1099511628211L;
                h = (h ^ (long)reference.z) * 1099511628211L;
                return h;
            }
        }
    }
}
