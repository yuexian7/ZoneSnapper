using System;
using System.Collections.Generic;

namespace ZoneSnapper.Engine
{
    /// <summary>区域类别。决定贴合半径倍率与描边预算（需求 7：行政区域比产业区域大得多，半径要分级）。</summary>
    public enum AreaTier
    {
        Other = 0,
        /// <summary>行政区域（游戏 AreaType.District）。</summary>
        District,
        /// <summary>地块（AreaType.Lot，含特殊产业的采矿/仓储/填埋用地）。</summary>
        Lot,
        /// <summary>地表贴图区域（AreaType.Surface）。</summary>
        Surface,
        /// <summary>太空区（AreaType.Space）。</summary>
        Space,
        /// <summary>地图瓦片（AreaType.MapTile）。</summary>
        MapTile
    }

    /// <summary>网络类型。反编译证据：Game.Net 下 Road.cs / TrainTrack.cs / TramTrack.cs / SubwayTrack.cs / Waterway.cs / PedestrianLane.cs 都是空标记组件。</summary>
    public enum NetKind
    {
        Other = 0,
        Road,
        Rail,
        Path,
        Waterway
    }

    /// <summary>
    /// 一个候选吸附点被贴到了什么上面。优先级与配色语义沿用原模组 Subdivisions 的图例
    /// （品红=区域边界、黄=网络、青=自由），并按用户需求补了「交叉口优先」。
    /// </summary>
    public enum SnapKind
    {
        /// <summary>不贴合，玩家点哪就是哪。</summary>
        Free = 0,
        // Shoreline = 1 已随「海岸线贴合」移除（v0.1.4）；编号留着不复用，避免旧日志里的数字被误读。
        NetSide = 2,
        NetCentre = 3,
        MapTileBorder = 4,
        AreaBorderOther = 5,
        AreaBorderSame = 6,
        /// <summary>网络节点/交叉口：原模组「intersections are preferred when you click near one」。</summary>
        NetNode = 7,
        /// <summary>
        /// 建筑轮廓边。需求 3：产业区/地皮类「以道路边缘**或同一建筑边缘**为贴合点」，
        /// 游戏自己也是这一档（FACT：AreaToolSystem.cs:651-663 的 <c>Snap.ObjectSide</c> 分支
        /// 拿 <c>ObjectUtils.CalculateBaseCorners</c> 的 quad.ab/bc/cd/da 四条直线吸）。
        /// </summary>
        ObjectSide = 8
    }

    /// <summary>
    /// <see cref="PlacedNode.Edge"/> 的编码规则。**集中在一处**是修出来的：
    /// 原来这个负数区间散落在 SnapKit/TraceKit 里各写一遍字面量，TraceKit 判海岸线用的是
    /// <c>a.Edge &lt;= -2000000</c> —— 一旦新增「建筑轮廓」档（本模组需求 3），
    /// 这个开区间判断会把建筑点也当成海岸线，描边直接走错分支。
    /// 现在只有这里知道区间边界，别处一律用 <c>Is*</c>。
    /// </summary>
    public static class AnchorCode
    {
        /// <summary>已有区域边界：-(b + 1)。</summary>
        public const long BORDER_MIN = -1000000;
        /// <summary>地图瓦片边界：-(1e6 + b)。</summary>
        public const long TILE_MIN = -2000000;
        /// <summary>海岸线：-(2e6 + s)。</summary>
        public const long SHORE_MIN = -3000000;
        /// <summary>建筑轮廓：-(3e6 + o)。</summary>
        public const long OBJECT_MIN = -4000000;

        public const int BORDER_BASE = -1;
        public const int TILE_BASE = -1000000;
        public const int SHORE_BASE = -2000000;
        public const int OBJECT_BASE = -3000000;

        /// <summary>≥0 就是可描边的网络边。</summary>
        public static bool IsNet(long edge) { return edge >= 0; }
        public static bool IsBorder(long edge) { return edge > BORDER_MIN && edge < 0; }
        public static bool IsTile(long edge) { return edge > TILE_MIN && edge <= BORDER_MIN; }
        public static bool IsShore(long edge) { return edge > SHORE_MIN && edge <= TILE_MIN; }
        public static bool IsObject(long edge) { return edge <= SHORE_MIN && edge > OBJECT_MIN; }

        public static int Decode(long edge, int baseValue) { return (int)(-(edge - baseValue)); }

        public static int Border(int b) { return -(1 + b); }
        public static int Tile(int b) { return -(1000000 + b); }
        public static int Shore(int s) { return -(2000000 + s); }
        public static int Object(int o) { return -(3000000 + o); }
    }

    /// <summary>
    /// 玩家「真正点下」的一个节点，以及它贴到了什么上。
    /// 手动栈只存这种记录；描边点是它的派生物，永远不进入手动栈 —— 这是需求 3
    /// （右键删上一个手动节点）能成立的根本原因。
    /// </summary>
    public struct PlacedNode
    {
        public P3 Pos;
        public SnapKind Kind;
        /// <summary>Kind=NetNode 时的图节点下标，否则 -1。</summary>
        public int GraphNode;
        /// <summary>Kind 为网络/边界类时的边下标，否则 -1。</summary>
        public int Edge;
        /// <summary>在该边折线上的弧长位置（环形取子段、绕行方向判定都要用）。</summary>
        public double Arc;
        /// <summary>该边的几何签名，用于「道路变了没有」的判定（需求 5 的自动跟随）。</summary>
        public long Signature;

        /// <summary>
        /// 贴到了这条边的哪一侧。需求 3 的分档口径要它：
        /// 市辖区吸中心线（<see cref="SnapKind.NetCentre"/>，这里恒为 0），
        /// 产业区/地皮/表面类吸路缘，而一条路有左右两条路缘 —— 相邻两点落在**不同侧**
        /// 就意味着这一段边界横穿了马路，正是需求 3 要区别对待的「跨越」情形。
        /// 用 byte 而不用枚举：要塞进 PlacedNode 这个高频小结构，且序列化时是现成的一字节。
        /// </summary>
        public byte Side;

        public const byte SIDE_CENTRE = 0;
        public const byte SIDE_LEFT = 1;
        public const byte SIDE_RIGHT = 2;

        /// <summary>贴的是这条路的外沿（左右任一侧）。</summary>
        public bool OnRoadEdge
        {
            get { return Kind == SnapKind.NetSide && (Side == SIDE_LEFT || Side == SIDE_RIGHT); }
        }

        public PlacedNode(P3 pos, SnapKind kind, int graphNode, int edge, double arc, long signature)
        {
            Pos = pos;
            Kind = kind;
            GraphNode = graphNode;
            Edge = edge;
            Arc = arc;
            Signature = signature;
            Side = SIDE_CENTRE;
        }

        public PlacedNode(P3 pos, SnapKind kind, int graphNode, int edge, double arc, long signature, byte side)
            : this(pos, kind, graphNode, edge, arc, signature)
        {
            Side = side;
        }

        public bool OnNetwork
        {
            get { return Kind == SnapKind.NetNode || Kind == SnapKind.NetCentre || Kind == SnapKind.NetSide; }
        }
    }

    /// <summary>图节点（对应游戏里的 Game.Net.Node 实体，即道路/铁路的接头）。</summary>
    public sealed class GraphNode
    {
        public int Id;
        public P3 Pos;
        /// <summary>接进来的边数。≥3 才算真交叉口，2 只是同一条路的分段点。</summary>
        public int Degree;
        public long Signature;
    }

    /// <summary>图边（对应游戏里带 Game.Net.Curve 的一段网络），已在游戏侧采样成折线。</summary>
    public sealed class GraphEdge
    {
        public int Id;
        public int StartNode;
        public int EndNode;
        public Polyline Line;
        /// <summary>
        /// 道路/轨道的**两条外沿**折线（可为 null：个别 prefab 没有边线数据）。
        ///
        /// ⚠ v0.1.1 及之前这里只有一个 <c>SideLine</c>，而它其实是用
        /// 「(m_Left.a + m_Right.a)/2 − 中心线起点」当偏移量算出来的 —— 那个中点**就是中心线**，
        /// 差值恒接近 0 ⇒ SideLine ≈ Line，所谓「贴路缘」实际上还是在贴中心线。
        /// 玩家反馈「产业区/地皮应该贴道路边缘」做不到，根因在这。
        /// 现在按游戏自己的画法存两条真边线（FACT：AreaToolSystem.cs:452-460 的 Snap.NetSide 分支
        /// 就是拿 edgeGeometry.m_Start.m_Left / m_Right 四条曲线去吸的）。
        /// </summary>
        public Polyline LeftLine;
        public Polyline RightLine;

        /// <summary>
        /// 整幅路面宽度（米）＝ <c>Game.Prefabs.NetCompositionData.m_Width</c>
        /// （FACT：ilspycmd -t Game.Prefabs.NetCompositionData → `public float m_Width;`，
        /// 且 Game.Net/GeometrySystem.cs:526 游戏自己就用 `m_Width * float2(0.5,-0.5) + m_MiddleOffset` 算两侧）。
        /// ≤0 表示拿不到 ⇒ 弯道节点密度退回「最小路宽 3 米」的保守口径（玩家明确要求的兜底值）。
        /// </summary>
        public double RoadWidth;

        /// <summary>
        /// 中心线到**左/右外沿**的真实距离（米）。反馈 3：外沿指的是人行道外缘，不是车道路缘。
        ///
        /// FACT：游戏自己烘焙边线就是这两位数
        /// （Game.Net/GeometrySystem.cs:526-530 <c>float2 o = m_Width * float2(0.5,-0.5) + m_MiddleOffset;</c>
        /// → <c>OffsetCurveLeftSmooth(centre, o.x)</c> 与 <c>(centre, o.y)</c>），
        /// 也就是说 <c>NetCompositionData.m_Width</c> 是**含人行道与隔音屏**的整幅宽度
        /// （旁证：Game.Zones/BlockSystem.cs:195 用 <c>m_Width - m_MiddleOffset*2</c> 当「地块可用宽度」的起点，
        /// 建筑退让就是从这里往后算的），而 <c>m_MiddleOffset</c> 表示中线两侧的**不对称**
        /// （单边人行道、中央分隔带、隔音墙都会让它非零）。
        ///
        /// 老写法两侧都用 <c>m_Width/2</c> ⇒ 在有不对称断面的路上，内侧那条缘线会整体偏 <c>m_MiddleOffset</c> 米，
        /// 玩家看到的就是「一边贴着人行道外缘、另一边卡在车道边上」。
        /// </summary>
        public double LeftHalfWidth;
        public double RightHalfWidth;

        /// <summary>
        /// 游戏给的 <c>NetCompositionData.m_MiddleOffset</c> 原值（米），由
        /// <see cref="SetWidthsFromPrefab"/> 一并记下：诊断日志要能看出「这条路是不是不对称断面」，
        /// 而只看两个半幅是算不出来的（两侧之和恒等于整幅宽）。
        /// </summary>
        public double MiddleOffset;

        /// <summary>
        /// 把游戏的 (整幅宽, 中线偏移) 翻成两侧各自的半幅。<b>这一算是纯算术，所以放在 Engine 层</b>：
        /// 采样器（GameSide/WorldSampler）只负责把 prefab 里那两个数读出来，翻法在这里，
        /// 于是回归壳能直接断言它（上一版这个算式写在采样器里，只有开游戏才看得见对错）。
        ///
        /// 口径照游戏自己：<c>o = m_Width * float2(0.5, -0.5) + m_MiddleOffset</c>
        /// （FACT：Game.Net/GeometrySystem.cs:526-530），取绝对值是因为偏移超过半幅时那一侧的
        /// 缘线会翻到中心的另一边上（游戏允许，我们的距离不能是负数）。
        /// 读不到（NaN / 负宽）时退回「对称且为 0」而不是把 NaN 传下去 —— 半幅进 NaN 会让
        /// 转角修剪与跨越判定全变成 false，表现是「某些路莫名不贴」。
        /// </summary>
        public void SetWidthsFromPrefab(double width, double middleOffset)
        {
            double w = GeoKit.FiniteOrZero(width);
            double mid = GeoKit.FiniteOrZero(middleOffset);
            if (w < 0) w = 0;
            RoadWidth = w;
            MiddleOffset = mid;
            LeftHalfWidth = Math.Abs(w * 0.5 + mid);
            RightHalfWidth = Math.Abs(w * 0.5 - mid);
        }

        /// <summary>
        /// 网络种类（Game.Net.Layer 的粗化版：Road / Rail / Path / Waterway）。
        /// 需求 2：描边路径上这里变了，**变化那一格的交叉口必须留一个节点**，
        /// 否则简化会把「路变铁路」那个角抹平，边界看上去直接穿过了道岔。
        /// </summary>
        public NetKind Kind;

        /// <summary>Kind 的原始 Layer 位（uint），比 NetKind 更细：道路↔公共交通道路也算变化。</summary>
        public uint LayerBits;

        /// <summary>是否高架/桥（CompositionFlags.General.Elevated）。需求 5：高架同样要能贴合。</summary>
        public bool Elevated;

        /// <summary>
        /// 是否隧道（CompositionFlags.General.Tunnel）。游戏自己贴**路缘**时排除隧道
        /// （FACT：AreaToolSystem.cs:501-508 <c>CheckComposition</c>：带 Tunnel 位的组合直接 return false），
        /// 我们照同一口径：隧道在地下，把地块边界贴到它的边线上看不见也说不通。
        /// 中心线档不受此限制（市辖区贴隧道中轴是合理的，需求 5 只要求高架也能贴，没要求排除）。
        /// </summary>
        public bool Tunnel;

        /// <summary>
        /// 这一段路面**最低**那一头的高程（米，地面 = 0）。与 <see cref="Lift"/>（取的是最高那头）成对，
        /// 由 GameSide 从 <c>Game.Net.Elevation.m_Elevation</c>（一个 float2：两端各自的高程）填。
        /// 只存最高那个数的话，「一半在地上、一半开槽沉下去」那种路会被记成 0，地下那一半就漏出去了。
        /// </summary>
        public double LowElevation;

        /// <summary>
        /// 地下物体：隧道，或整段沉在地面以下的开槽路。<b>第八轮反馈 3 的判据入口</b> ——
        /// 「所有区域工具的节点吸附和自动贴合都不包括建在地下的所有物体」，三类区域一律适用
        ///（老口径只在路缘档排隧道、中心线档放过，那一半已被这条反馈否掉，见 PolicyKit.IsTargetNet）。
        ///
        /// 两条判据的来源分别抄到游戏自己：
        ///  · 隧道位：<c>NetCompositionData.m_Flags.m_General &amp; CompositionFlags.General.Tunnel</c>
        ///    —— 游戏自己的区域吸附就是这么排除的（FACT：Game.Tools/AreaToolSystem.cs:501-508
        ///    <c>CheckComposition</c>：带这个位的组合 <c>return false</c>）；
        ///  · 开槽下沉段：游戏在放置侧用的是「elevation 低于 0 且低到 <c>-3×ElevationLimit</c>，
        ///    或者组合带 <c>GeometryFlags.LoweredIsTunnel</c>」
        ///    （FACT：Game.Tools/NetToolSystem.cs:6078 与 :6240 <c>requireUnderground = ...</c>）。
        ///    我们不需要区分到那么细：**沉到地面以下半米**就不该再当地面边界的目标。
        ///    半米这个数是刻意低于游戏的水位线口径（<c>ModConfig.WATER_LEVEL = 0.2</c> 之上留余量），
        ///    免得把「跟地面齐平的正常路」因浮点噪声判成地下。
        /// </summary>
        public bool Underground
        {
            get { return Tunnel || LowElevation < -0.5; }
        }

        /// <summary>
        /// 路面相对地面的抬升（Elevation / m_HeightRange）。
        /// 第五轮反馈 4 之后它不再只是诊断：市辖区允许在「相同或不同高度」的道路/轨道之间切换，
        /// 而**切换处必须放一个节点**，判「高度变了」靠的就是它 + <see cref="Elevated"/>
        /// （见 TraceKit.HeightChanged）。2D 走线本身仍然不用它。
        /// </summary>
        public double Lift;

        /// <summary>
        /// 这条路是不是**架在水上**（埠头、栈桥、水上步道）。
        /// 判据来自 prefab：<c>NetGeometryData.m_Flags</c> 的 <c>Game.Net.GeometryFlags.OnWater</c>（= 0x2000000）
        /// （FACT：research/decompiled/Game.Net/GeometryFlags.cs:31）。
        /// 第六轮反馈 9 明确把埠头排除在三类区域的贴合目标之外：埠头的"路面"是木板栈道，
        /// 把市辖区边界贴上去会得到一条画在水上的边界。
        /// </summary>
        public bool OnWater;

        /// <summary>
        /// 这条路是不是**属于某个建筑/地块的内部路**（园区里的步道、产业区内部的专用道、停车场的车道）。
        /// 两条判据任一成立就算：prefab 的 <c>GeometryFlags.SubOwner</c>（= 0x1000000，
        /// 游戏自己就是用它判「这条 net 归别人所有」，FACT：Game.Pathfind/LaneDataSystem.cs:525
        /// 与 ParkingLaneDataSystem.cs:399 都是 <c>Owner.m_Owner</c> + <c>SubOwner</c> 一起查），
        /// 或者实体上挂着 <c>Game.Common.Owner</c> 且那个 owner 是栋建筑。
        /// 第六轮反馈 9 要求排除它们：玩家画的是**城市尺度**的区域，
        /// 边界跟着一个厂区里的内部道走，看上去就是"莫名其妙吸到另一条路上"。
        /// </summary>
        public bool Internal;

        /// <summary>按 <see cref="PlacedNode.Side"/> 取这条边该沿哪条折线描边；边线缺失时退回中心线。</summary>
        public Polyline LineFor(byte side)
        {
            if (side == PlacedNode.SIDE_LEFT && LeftLine != null && LeftLine.Count >= 2) return LeftLine;
            if (side == PlacedNode.SIDE_RIGHT && RightLine != null && RightLine.Count >= 2) return RightLine;
            return Line;
        }

        /// <summary>这条边有没有可用的外沿折线（需求 3 的产业区/地皮档靠它决定能不能贴路缘）。</summary>
        public bool HasEdgeLines
        {
            get
            {
                return (LeftLine != null && LeftLine.Count >= 2) || (RightLine != null && RightLine.Count >= 2);
            }
        }
        public double Length;
        /// <summary>是否落在一个环（环岛/铁路环线）上。需求 8。</summary>
        public bool OnRing;
        public int RingId;
        public long Signature;
    }

    /// <summary>已有区域的一段边界（用于「贴到别人家的边界上」，原模组的 Magenta 档）。</summary>
    public sealed class BorderRef
    {
        public int AreaId;
        public AreaTier Tier;
        public Polyline Line;
        public long Signature;
    }

    /// <summary>
    /// 一栋建筑的落地轮廓（需求 3：产业区/地皮类「以道路边缘或**同一建筑**边缘为贴合点」）。
    ///
    /// 轮廓怎么来的（不是猜的）：<c>ObjectUtils.CalculateBaseCorners(transform.m_Position,
    /// transform.m_Rotation, objectGeometryData.m_Bounds)</c> 出一个 <c>Quad3</c>，
    /// 游戏自己的 <c>Snap.ObjectSide</c> 就是把 quad.ab/bc/cd/da 四条直线当吸附线
    /// （FACT：AreaToolSystem.cs:651-663）。这里按同一口径连成闭合折线。
    /// </summary>
    public sealed class ObjectRef
    {
        public int Id;
        /// <summary>根建筑下标（<c>Game.Common.Owner</c> 链走到 <c>BuildingData</c> 那一格）。同一栋楼的子建筑共用它 ⇒ 「同一建筑边缘」判据。</summary>
        public int OwnerIndex;
        /// <summary>闭合轮廓（首点在末尾重复一次）。</summary>
        public Polyline Line;
        public long Signature;

        /// <summary>
        /// 这个物体是不是**一栋建筑**（而不是摆件、树、高架桥墩、码头桩之类）。
        /// 判据来自 prefab：有 <c>Game.Prefabs.BuildingData</c>，或者 <c>ObjectGeometryData.m_Flags</c>
        /// 带 <c>Game.Objects.GeometryFlags.HasLot</c>（= 0x100000，"这物体占一块地"）
        /// （FACT：research/decompiled/Game.Objects/GeometryFlags.cs:28）。
        ///
        /// 为什么要有它：游戏自己的 <c>Snap.ObjectSide</c> 对**任何**非圆形静态物体都贴边
        /// （FACT：AreaToolSystem.cs:651-663 只查 <c>GeometryFlags.Circular</c>，
        /// <c>BuildingData</c> 在那里只用来把包围盒换成地块尺寸），所以原版会把地块角点吸到一棵树的外接框上。
        /// 第六轮反馈 9 要求产业区/表面区只认**建筑边缘**，这一档就得能分辨。
        /// </summary>
        public bool IsBuilding;

        /// <summary>
        /// 这个物体是不是**建在地下**（地下停车场那类）。第八轮反馈 3：
        /// 「所有区域工具的节点吸附和自动贴合都不包括建在地下的所有物体」。
        ///
        /// ⚠ 游戏侧**没有**「这栋楼在地下」这个位可以抄：<c>Game.Buildings/BuildingFlags.cs:6-14</c>
        /// 只有 HighRentWarning / StreetLightsOff / LowEfficiency / Illuminated / Historical 五个，
        /// 全库也搜不到 <c>Substructure</c> / <c>Subterranean</c> 这类符号。
        /// 能用的只有落地高度这一条：游戏的碰撞分层用的就是它
        ///（FACT：Game.Objects/ObjectUtils.cs:262-286 <c>GetCollisionMask</c> 里
        ///  <c>elevation.m_Elevation &lt; 0f ⇒ CollisionMask.Underground</c>；
        ///  几何兜底见 Game.Objects/RaycastJobs.cs:228 的 <c>m_Bounds.min.y &lt; 0f</c>）。
        /// ⇒ 这一条是**我们自己的口径**，不是官方规则；而且游戏自己的区域吸附对地下建筑不加过滤
        ///（FACT：AreaToolSystem.cs:552-668 的 ObjectIterator 全程没有这一道），
        /// 所以它写在注释里免得下一轮有人当成"抄来的"。阈值与 net 侧同一口径（沉到地面以下半米）。
        /// </summary>
        public bool Underground;
    }

    /// <summary>
    /// 一次查询用的世界几何快照。全部由 GameSide 层从游戏 ECS/四叉树里抓出来填好，
    /// 引擎不认识任何游戏类型 —— 这样描边与贴合算法可以在离线壳里用手工构造的快照做断言。
    /// </summary>
    public sealed class WorldSnapshot
    {
        public readonly List<GraphNode> Nodes = new List<GraphNode>();
        public readonly List<GraphEdge> Edges = new List<GraphEdge>();
        public readonly List<BorderRef> Borders = new List<BorderRef>();
        /// <summary>走廊内的建筑落地轮廓（需求 3 的「同一建筑边缘」档；抓不到就是空表，不影响其它档）。</summary>
        public readonly List<ObjectRef> Objects = new List<ObjectRef>();
        /// <summary>地图瓦片边界（游戏 AreaType.MapTile 的矩形四点区域，FACT：Areas/MapTileSystem dump :52-55）。</summary>
        public readonly List<BorderRef> MapTiles = new List<BorderRef>();
        // 海岸线折线（原 Shorelines 字段）随「海岸线贴合」一起在 v0.1.4 移除：
        // 玩家实测那条开关看不出效果，明确要求去掉 ⇒ 不再抓、不再吸、不再描。
        // 等值线数学本身留在 IsoKit（见其类注释），要重启这条功能时不用重写。

        private List<List<AdjEntry>> m_adj;

        /// <summary>某个图节点的一条出边。</summary>
        public struct AdjEntry
        {
            public int EdgeIndex;
            public int OtherNode;
            /// <summary>该节点在这条边折线上的弧长位置。</summary>
            public double ArcAtThisNode;
            public double ArcAtOtherNode;
        }

        /// <summary>邻接表。Dijkstra 与环检测都走它。</summary>
        public List<List<AdjEntry>> Adjacency
        {
            get
            {
                if (m_adj == null) BuildAdjacency();
                return m_adj;
            }
        }

        public void InvalidateIndex()
        {
            m_adj = null;
            m_junctionBuilt = false;      // 边表变了 ⇒ 路口分组也得重算（中心点不许在两次重描之间跳位）
        }

        // ————————————————————— 路口（第八轮反馈 5）
        //
        // 与邻接表一样是**懒建**的：WorldSampler 只管把节点/边填进来，谁要谁再算。
        // 建表的规则全在 Engine/JunctionKit.cs（纯几何，离线壳 J 段直接断言），这里只是缓存与取用口。

        private List<Junction> m_junctions;
        private int[] m_junctionOf;
        private bool m_junctionBuilt;

        public List<Junction> Junctions
        {
            get { EnsureJunctions(); return m_junctions; }
        }

        public void EnsureJunctions()
        {
            if (m_junctionBuilt) return;
            m_junctionBuilt = true;
            m_junctions = JunctionKit.Build(this, out m_junctionOf);
        }

        /// <summary>这个图节点属于哪个路口；-1 = 不属于任何路口（路上分段点、断头路端点）。</summary>
        public int JunctionOf(int nodeIndex)
        {
            EnsureJunctions();
            if (m_junctionOf == null || nodeIndex < 0 || nodeIndex >= m_junctionOf.Length) return -1;
            return m_junctionOf[nodeIndex];
        }

        /// <summary>
        /// 走线与吸附候选统一用的「这个接头的坐标」：属于某路口 ⇒ 用那个路口的**唯一中心点**。
        ///
        /// 这一句就是反馈 5 的落点：同一路口不管从哪个车道方向连过来，经过的都是同一个坐标，
        /// 于是「在偏心的那个节点上拐弯」这件事在结构上不可能再发生
        /// （老写法用 <c>Nodes[i].Pos</c>，而一个路口有 2~5 个成员节点，挑中哪个取决于邻接表顺序）。
        /// 不属于路口的节点原样返回位置 —— 路上分段点本来就没有「中心」这一说。
        /// </summary>
        public P3 NodePoint(int nodeIndex)
        {
            if (nodeIndex < 0 || nodeIndex >= Nodes.Count) return default(P3);
            int j = JunctionOf(nodeIndex);
            if (j >= 0 && j < m_junctions.Count) return m_junctions[j].Center;
            GraphNode gn = Nodes[nodeIndex];
            return gn == null ? default(P3) : gn.Pos;
        }

        // ————————————————————— 锚点重绑用的「签名 → 下标」索引
        //
        // 【为什么要有这一段】手动栈跨帧持有，而栈里 PlacedNode.Edge / GraphNode 记的是**某一次快照里的
        // 下标**。EnsureWorld 每长大一圈就重抓一次快照（采样贝塞尔、遍历四叉树），下标表整个洗牌：
        // 老锚点要么越界（TraceKit 直接在 `a.Edge >= world.Edges.Count` 处退回直线），要么指向另一条路
        // （描出鬼线）。v0.1.2 实机第三轮的账就是这个：snap=2907 而 traceHit 只有 17、fallback=119，
        // 玩家视角=「完全不贴合」。签名（GameSide 用 Entity.Index/Version + 该实体的参考坐标算的）
        // 在重抓之间是稳的，所以靠它把锚点搬回新下标空间；搬不到（那条路真被改了/拆了）再由
        // AnchorRebind 退回「按位置重新吸附」。
        //
        // 快照是一次一新对象（WorldSampler.Capture），所以这几个字典不需要失效逻辑。
        private Dictionary<long, int> m_edgeSig;
        private Dictionary<long, int> m_nodeSig;
        private Dictionary<long, int> m_borderSig;
        private Dictionary<long, int> m_objectSig;
        private Dictionary<long, int> m_tileSig;

        private static int Lookup<T>(ref Dictionary<long, int> cache, IList<T> items, Func<T, long> sigOf, long signature)
        {
            if (signature == 0 || items == null) return -1;
            if (cache == null)
            {
                cache = new Dictionary<long, int>(items.Count);
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i] == null) continue;
                    long s = sigOf(items[i]);
                    if (s != 0 && !cache.ContainsKey(s)) cache[s] = i;   // 同签名保留先来者：重绑必须确定
                }
            }
            int idx;
            return cache.TryGetValue(signature, out idx) ? idx : -1;
        }

        public int IndexOfEdge(long signature) { return Lookup<GraphEdge>(ref m_edgeSig, Edges, x => x.Signature, signature); }
        public int IndexOfNode(long signature) { return Lookup<GraphNode>(ref m_nodeSig, Nodes, x => x.Signature, signature); }
        public int IndexOfBorder(long signature) { return Lookup<BorderRef>(ref m_borderSig, Borders, x => x.Signature, signature); }
        public int IndexOfObject(long signature) { return Lookup<ObjectRef>(ref m_objectSig, Objects, x => x.Signature, signature); }
        public int IndexOfMapTile(long signature) { return Lookup<BorderRef>(ref m_tileSig, MapTiles, x => x.Signature, signature); }

        public void BuildAdjacency()
        {
            m_adj = new List<List<AdjEntry>>(Nodes.Count);
            for (int i = 0; i < Nodes.Count; i++) m_adj.Add(new List<AdjEntry>());
            for (int e = 0; e < Edges.Count; e++)
            {
                GraphEdge ge = Edges[e];
                if (ge.Line == null || ge.Line.Count < 2) continue;
                double len = ge.Length > 0 ? ge.Length : ge.Line.Length2D();
                if (ge.StartNode >= 0 && ge.StartNode < m_adj.Count)
                {
                    m_adj[ge.StartNode].Add(new AdjEntry { EdgeIndex = e, OtherNode = ge.EndNode, ArcAtThisNode = 0, ArcAtOtherNode = len });
                }
                if (ge.EndNode >= 0 && ge.EndNode < m_adj.Count)
                {
                    m_adj[ge.EndNode].Add(new AdjEntry { EdgeIndex = e, OtherNode = ge.StartNode, ArcAtThisNode = len, ArcAtOtherNode = 0 });
                }
            }
            for (int i = 0; i < Nodes.Count; i++)
            {
                if (i < Nodes.Count) Nodes[i].Degree = m_adj[i].Count;
            }
        }

        /// <summary>
        /// 检测环（Tarjan 式的「回边/桥」思路的简化版：把每条边按两端点所在连通分量里的重边与回路标出来）。
        /// 这里用可解释、可断言的做法：对每个连通分量做 DFS，凡是被 DFS 用作「已经visit过的祖先」的边属于环；
        /// 再把只在这些边上出现的边标 OnRing 并分配 RingId。
        /// 需求 8 要求环形道路/铁路按中心点贴合、并且描边要绕环岛包住街区，都依赖这个标记。
        /// </summary>
        public void DetectRings()
        {
            int n = Nodes.Count;
            if (n == 0) return;
            int[] parentEdge = new int[n];
            int[] state = new int[n];
            int[] parent = new int[n];
            for (int i = 0; i < n; i++) { parentEdge[i] = -1; state[i] = 0; parent[i] = -1; }

            HashSet<int> ringEdges = new HashSet<int>();
            List<int> stack = new List<int>();
            List<int> edgeStack = new List<int>();

            for (int start = 0; start < n; start++)
            {
                if (state[start] != 0) continue;
                state[start] = 1;
                stack.Add(start);
                edgeStack.Clear();
                // 显式栈 DFS：游戏世界里有几万节点，递归会栈溢出。
                int[] iterIdx = new int[n];
                while (stack.Count > 0)
                {
                    int u = stack[stack.Count - 1];
                    List<AdjEntry> adj = Adjacency[u];
                    if (iterIdx[u] < adj.Count)
                    {
                        AdjEntry en = adj[iterIdx[u]];
                        iterIdx[u]++;
                        if (en.EdgeIndex == parentEdge[u]) continue;
                        int v = en.OtherNode;
                        if (state[v] == 0)
                        {
                            state[v] = 1;
                            parent[v] = u;
                            parentEdge[v] = en.EdgeIndex;
                            stack.Add(v);
                            edgeStack.Add(en.EdgeIndex);
                        }
                        else if (state[v] == 1 && v != u)
                        {
                            // 后边：从 u 回到祖先 v，路径上所有边 + 这条边构成一个环
                            ringEdges.Add(en.EdgeIndex);
                            int cur = u;
                            while (cur != v && cur >= 0)
                            {
                                int pe = parentEdge[cur];
                                if (pe < 0) break;
                                ringEdges.Add(pe);
                                cur = parent[cur];
                            }
                        }
                    }
                    else
                    {
                        state[u] = 2;
                        stack.RemoveAt(stack.Count - 1);
                        if (edgeStack.Count > 0) edgeStack.RemoveAt(edgeStack.Count - 1);
                    }
                }
            }

            // 只保留「两端都在环上」的边：后边判据会把一些树枝误标，收紧一次。
            List<int> keep = new List<int>();
            foreach (int e in ringEdges)
            {
                GraphEdge ge = Edges[e];
                if (ge.StartNode >= 0 && ge.EndNode >= 0) keep.Add(e);
            }
            keep.Sort();

            // 给环分配 RingId：用并查集把共节点的环边并起来。
            int[] uf = new int[n];
            for (int i = 0; i < n; i++) uf[i] = i;
            foreach (int e in keep)
            {
                GraphEdge ge = Edges[e];
                int ra = Find(uf, ge.StartNode);
                int rb = Find(uf, ge.EndNode);
                if (ra != rb) uf[ra] = rb;
            }
            Dictionary<int, int> ringIds = new Dictionary<int, int>();
            int next = 0;
            foreach (int e in keep)
            {
                GraphEdge ge = Edges[e];
                int root = Find(uf, ge.StartNode);
                int id;
                if (!ringIds.TryGetValue(root, out id)) { id = next++; ringIds[root] = id; }
                ge.OnRing = true;
                ge.RingId = id;
            }
        }

        private static int Find(int[] uf, int x)
        {
            while (uf[x] != x)
            {
                uf[x] = uf[uf[x]];
                x = uf[x];
            }
            return x;
        }

        /// <summary>
        /// 两个图节点之间是否存在「除 excludeEdge 之外」的另一条通路。
        /// 用来判断「两点落在同一条路上时，能不能绕另一侧」——只有真的构成环才值得去跑绕行搜索。
        /// BFS（不是 DFS）：走廊图上千节点，DFS 递归深度不可控。
        /// </summary>
        public bool HasAlternatePathBetween(int nodeA, int nodeB, int excludeEdge)
        {
            if (nodeA < 0 || nodeB < 0 || nodeA >= Adjacency.Count || nodeB >= Adjacency.Count) return false;
            if (nodeA == nodeB) return false;
            bool[] seen = new bool[Nodes.Count];
            Queue<int> q = new Queue<int>();
            seen[nodeA] = true;
            q.Enqueue(nodeA);
            while (q.Count > 0)
            {
                int u = q.Dequeue();
                List<AdjEntry> adj = Adjacency[u];
                for (int i = 0; i < adj.Count; i++)
                {
                    if (adj[i].EdgeIndex == excludeEdge) continue;
                    int v = adj[i].OtherNode;
                    if (v < 0 || v >= seen.Length || seen[v]) continue;
                    if (v == nodeB) return true;
                    seen[v] = true;
                    q.Enqueue(v);
                }
            }
            return false;
        }

        /// <summary>整张快照的几何签名（缓存失效的粗筛：没变就连描边都不用重算）。</summary>
        public long ComputeSignature()
        {
            unchecked
            {
                long h = 1469598103934665603L;
                for (int i = 0; i < Edges.Count; i++)
                {
                    h = (h ^ Edges[i].Signature) * 1099511628211L;
                    h = (h ^ Edges[i].StartNode) * 1099511628211L;
                    h = (h ^ Edges[i].EndNode) * 1099511628211L;
                }
                for (int i = 0; i < Borders.Count; i++) h = (h ^ Borders[i].Signature) * 1099511628211L;
                return h;
            }
        }
    }

    /// <summary>
    /// 从「湿/干」标量场现算海岸线折线（marching squares 的简化版）。
    /// 为什么要现算：反编译实证游戏里没有任何海岸线折线或等高线提取代码
    /// （FACT：NetToolSystem.cs:1811-1866 SnapShoreline 是在网格里遍历 int2 单元、
    /// 以 worldPosition.y &gt; 0.2f 判干湿再取加权质心），渲染侧的 contour 是 GPU kernel
    /// （FACT：Game.Rendering/UndergroundPass.cs:76 FindKernel("ContourPass")）。
    ///
    /// ⚠ **v0.1.4 起产品侧不再使用它**：玩家实测「海岸线那个开关完全没发现效果，而且意义并不大」
    /// ⇒ 按需求把海岸线贴合/描边整条功能去掉了（设置项、快照抓取、贴合候选、描边分支都已删）。
    /// 这里留着是因为等值线缝合是这套几何里最难写对的一段（同一个点两侧单元各算一次，
    /// 差 1 个 ULP 就断线），由回归壳 C9/Iso 段继续守着；哪天要重启这条功能，数学部分是现成已证过的。
    /// </summary>
    public static class IsoKit
    {
        /// <summary>湿/干判定阈值（与游戏 SnapShoreline 的 0.2f 同口径，保证与游戏自己的吸附表现一致）。</summary>
        public const double WATER_LEVEL = 0.2;

        /// <summary>
        /// 输入一个标量场（世界高度，行优先 [z][x]），输出穿过「高度 = WATER_LEVEL」等值线的折线集合。
        /// 采用简化 marching squares：每个单元的边上插值出交点，再按单元把交点连成线段，最后缝合。
        /// </summary>
        public static List<Polyline> BuildIsolines(ScalarField f)
        {
            List<Polyline> outLines = new List<Polyline>();
            if (f.Values == null || f.Width < 2 || f.Height < 2) return outLines;

            List<Seg> segs = new List<Seg>();
            for (int z = 0; z + 1 < f.Height; z++)
            {
                for (int x = 0; x + 1 < f.Width; x++)
                {
                    P3 c00 = f.At(x, z);
                    P3 c10 = f.At(x + 1, z);
                    P3 c11 = f.At(x + 1, z + 1);
                    P3 c01 = f.At(x, z + 1);
                    bool w00 = IsWet(c00), w10 = IsWet(c10), w11 = IsWet(c11), w01 = IsWet(c01);
                    List<P3> cross = new List<P3>(4);
                    AddEdge(cross, c00, w00, c10, w10);
                    AddEdge(cross, c10, w10, c11, w11);
                    AddEdge(cross, c11, w11, c01, w01);
                    AddEdge(cross, c01, w01, c00, w00);
                    // 交点个数在「四角布尔标定」下恒为偶数（0/2/4），所以不存在「落下一个交点、环上开一个缺口」
                    // 的奇数情况 —— 探针实测 oddCells=0。以前 I 段碎成 6 条的真因在缝合层：同一个交点被相邻
                    // 两个单元从相反方向插值出两份差 1 ULP 的坐标（见 AddEdge 的「从湿端算起」）。
                    // 4 个 = 鞍形，按顺序配成两条不相交的段（底-右 / 顶-左）。
                    for (int i = 0; i + 1 < cross.Count; i += 2) AddSeg(segs, cross[i], cross[i + 1]);
                    // 奇数只剩一个孤点：与首点配成一段。按上面的不变式这条永远不触发，留着是因为
                    // 「静默丢交点」的代价是整条海岸断成几截（实机极难查），而多画一段的代价只是一根毛刺。
                    if ((cross.Count & 1) == 1 && cross.Count >= 3) AddSeg(segs, cross[cross.Count - 1], cross[0]);
                }
            }
            return Stitch(segs, f.StitchEpsilon);
        }

        /// <summary>
        /// 一条等值线段。必须 public：Stitch 是 public 方法而参数用它，
        /// 私有嵌套类型会让 net48 编译器报 CS0051 可访问性不一致（Playbook 步骤 2 列过的坑）。
        /// </summary>
        public struct Seg { public P3 A; public P3 B; }

        /// <summary>
        /// 只登记**有长度**的等值线段。
        /// 网格顶点的高度正好等于阈值时，相邻两条边都会插出一个「同一个点」，
        /// 于是产出一条零长线段的垃圾折线（回归壳 I 段抓到：128 点的真环旁边多了两条两点重合的junk）。
        /// </summary>
        private static void AddSeg(List<Seg> segs, P3 a, P3 b)
        {
            if (a.DistanceSquaredTo(b) <= 1e-12) return;
            segs.Add(new Seg { A = a, B = b });
        }

        /// <summary>湿 = 低于水位线。等于水位线算「干」，与游戏 SnapShoreline 的 <c>y &gt; 0.2f</c> 同口径。</summary>
        private static bool IsWet(P3 p) { return p.H < WATER_LEVEL; }

        /// <summary>
        /// 求一条单元边与等值面的交点。
        ///
        /// ⚠ 交点一律**从湿端量起**（<see cref="Crossing"/>），不是「本单元碰巧先列出的那个角点」。
        /// 同一条网格边被相邻两个单元从相反方向各看一次：Lerp(a,b,t) 与 Lerp(b,a,1-t) 数学上同点、
        /// 位上差 1 个 ULP（实测 60 与 59.999999999999964 并存），缝合哈希立刻分家 ⇒ 一条闭环被打断。
        /// 探针数出来的 32 个悬挂端点全部是这种 fpMiss（missing=0），这就是回归壳 I 段碎成 6 条的全部原因。
        ///
        /// 这里也**不做**「同一个点只记一次」的去重：格点正好在阈值上时，相邻两条边各插出一个重合交点
        /// 是经典 marching squares 的正常形状（该单元两点重合 ⇒ 由 <see cref="AddSeg"/> 丢掉零长段）。
        /// 去重会把它们塌成一个，于是这个单元一个交点都不剩、环上开缺口。
        /// </summary>
        private static void AddEdge(List<P3> into, P3 a, bool wa, P3 b, bool wb)
        {
            if (wa == wb) return;
            into.Add(wa ? Crossing(a, b) : Crossing(b, a));
        }

        /// <summary>湿→干方向上的交点；高度锁死成阈值本身（等值线定义，也是下游贴合要用的那一圈高程）。</summary>
        private static P3 Crossing(P3 wet, P3 dry)
        {
            double denom = dry.H - wet.H;
            if (!(denom > 0.0)) return new P3(wet.X, wet.Y, WATER_LEVEL);   // NaN / 同高兜底：压在湿点上，绝不外推
            double t = (WATER_LEVEL - wet.H) / denom;
            P3 p;
            // 格点正好落在水位线上（游戏里水面是一张平面，这种点成片出现）：t 精确等于 0 或 1，
            // 但 Lerp 出来的坐标不等于那个格点本身。直接给格点，两侧单元才算同一个点。
            if (t <= CORNER_TIE) p = wet;
            else if (t >= 1.0 - CORNER_TIE) p = dry;
            else p = P3.Lerp(wet, dry, t);
            return new P3(p.X, p.Y, WATER_LEVEL);
        }

        /// <summary>判「交点是否就是格点本身」的参数容差：1e-12 × 单元边长，量级上就是一根头发。</summary>
        private const double CORNER_TIE = 1e-12;

        /// <summary>
        /// 把线段按端点缝合为折线（端点量化到 eps 网格后哈希相连）。eps&lt;=0 用默认精度。
        ///
        /// 两处与最早那版不同，都是回归壳 I 段逼出来的：
        ///  1) 桶键改成「四舍五入 + (ix,iy) 打包」，不再用 <c>ix*73856093 ^ iy*19349663</c> 这类异或哈希。
        ///     量化坐标到 ±3e6 时异或真会撞车，而老代码的接法是一个三元表达式 —— **没匹配上也照样
        ///     Append 远端点**，于是缝出来的线既断又绕。现在距离不合格就不接（见 <see cref="FindUnused"/>）。
        ///  2) 找下一条线段时扫 3×3 邻域桶。量化桶天然会在格子边界两侧把本该同点的端点分家
        ///     （floor(59.99999/0.5)=119 与 floor(60/0.5)=120），只查自己那一格 = 永远缝不上。
        /// </summary>
        public static List<Polyline> Stitch(List<Seg> segs, double eps)
        {
            if (eps <= 0) eps = DEFAULT_STITCH_EPS;
            List<Polyline> lines = new List<Polyline>();
            if (segs == null || segs.Count == 0) return lines;

            Dictionary<long, List<int>> byKey = new Dictionary<long, List<int>>();
            for (int i = 0; i < segs.Count; i++)
            {
                AddKey(byKey, Key(segs[i].A, eps), i);
                AddKey(byKey, Key(segs[i].B, eps), i);
            }

            bool[] used = new bool[segs.Count];
            for (int i = 0; i < segs.Count; i++)
            {
                if (used[i]) continue;
                used[i] = true;
                P3 far;

                // 从 B 端往后走
                List<P3> fwd = new List<P3>();
                P3 anchor = segs[i].B;
                int si;
                while ((si = FindUnused(byKey, segs, used, anchor, eps, out far)) >= 0)
                {
                    used[si] = true;
                    fwd.Add(far);
                    anchor = far;
                }

                // 从 A 端往前走路（得到的顺序是反的，接回折线时倒过来）
                List<P3> back = new List<P3>();
                anchor = segs[i].A;
                while ((si = FindUnused(byKey, segs, used, anchor, eps, out far)) >= 0)
                {
                    used[si] = true;
                    back.Add(far);
                    anchor = far;
                }

                Polyline cur = new Polyline();
                for (int k = back.Count - 1; k >= 0; k--) cur.Add(back[k]);
                cur.Add(segs[i].A);
                cur.Add(segs[i].B);
                for (int k = 0; k < fwd.Count; k++) cur.Add(fwd[k]);
                lines.Add(cur);
            }
            return lines;
        }

        /// <summary>
        /// 在 3×3 邻域桶里找一条还没用过、且**确实有一个端点落在 p 的 eps 之内**的线段，
        /// 通过 <paramref name="far"/> 交出应该接上的那一头。多个候选时取最近的一个（鞍形/分叉点）。
        /// 返回 -1 表示走到头了。
        /// </summary>
        private static int FindUnused(Dictionary<long, List<int>> byKey, List<Seg> segs, bool[] used, P3 p, double eps, out P3 far)
        {
            long bx = Bucket(p.X, eps);
            long by = Bucket(p.Y, eps);
            int best = -1;
            double bestD = double.MaxValue;
            P3 bestFar = p;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    List<int> cand;
                    if (!byKey.TryGetValue(Pack(bx + dx, by + dy), out cand)) continue;
                    for (int k = 0; k < cand.Count; k++)
                    {
                        int idx = cand[k];
                        if (idx < 0 || idx >= used.Length || used[idx]) continue;
                        Seg s = segs[idx];
                        double da = s.A.DistanceTo(p);
                        double db = s.B.DistanceTo(p);
                        double near = da <= db ? da : db;
                        if (near > eps || near >= bestD) continue;
                        bestD = near;
                        best = idx;
                        bestFar = da <= db ? s.B : s.A;
                    }
                }
            }
            far = bestFar;
            return best;
        }

        private static void AddKey(Dictionary<long, List<int>> d, long key, int idx)
        {
            List<int> l;
            if (!d.TryGetValue(key, out l)) { l = new List<int>(2); d[key] = l; }
            if (!l.Contains(idx)) l.Add(idx);
        }

        private static long Key(P3 p, double eps)
        {
            return Pack(Bucket(p.X, eps), Bucket(p.Y, eps));
        }

        /// <summary>
        /// 坐标 → 桶号。四舍五入而不是向下取整，并且夹在 int32 内（游戏地图 ±5e4 米、默认 eps 1e-4
        /// 时是 ±5e8，永远够不到边界；玩家把 eps 传成 1e-12 这种荒唐值时靠夹断保住键的唯一性）。
        /// </summary>
        private static long Bucket(double v, double eps)
        {
            double q = eps <= 0 ? 1.0 : eps;
            double d = v / q;
            if (double.IsNaN(d)) return int.MaxValue;
            double r = Math.Round(d, MidpointRounding.AwayFromZero);
            if (r < int.MinValue) return int.MinValue;
            if (r > int.MaxValue) return int.MaxValue;
            return (long)r;
        }

        /// <summary>(ix,iy) → 唯一 long 键：高 32 位放 x 桶号、低 32 位放 y 桶号，int32 范围内不碰撞。</summary>
        private static long Pack(long ix, long iy)
        {
            return (ix << 32) ^ (iy & 0xffffffffL);
        }

        // 端点哈希用的量化精度：交点现在两侧单元算出的是同一串位（见 AddEdge），所以这个 eps 只用来
        // 吞真正的数值噪声；玩家想让海岸线闭合得更「宽松」时可以传更大的值。
        private const double DEFAULT_STITCH_EPS = 1e-4;
    }

    /// <summary>
    /// 等值线提取的输入：一格一格的标量场（本模组用「世界高度」，游戏侧从
    /// WaterSystem.GetSurfaceData 与 TerrainSystem.GetHeightData 组合出 max(terrain, waterSurface)，
    /// 因为直接用 terrain 高度会让水下采样点判成「干」——原模组 v1.2.0 修的「吸附标记掉到地形下面」就是这个坑。
    /// </summary>
    public struct ScalarField
    {
        public double[] Values;
        public int Width;
        public int Height;
        public double StepX;
        public double StepZ;
        public double OriginX;
        public double OriginZ;

        /// <summary>0 表示用默认缝合精度。</summary>
        public double StitchEpsilon;

        public P3 At(int x, int z)
        {
            int idx = z * Width + x;
            double v = (idx >= 0 && idx < Values.Length) ? Values[idx] : double.PositiveInfinity;
            return new P3(OriginX + x * StepX, OriginZ + z * StepZ, v);
        }
    }
}
