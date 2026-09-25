using System;
using System.Collections.Generic;
using ZoneSnapper.Engine;

namespace ZoneSnapper.T3
{
    /// <summary>
    /// 手工世界/手动栈构造器。所有输入都是纸面数据：T3 不读存档、不连游戏、不联网。
    /// 刻意写得冗长显式（而不是「随机生成 200 个用例」）：门禁报告里失败的那一条必须能被人
    /// 一眼复现，随机种子驱动的断言在离线壳里是负债。
    /// </summary>
    internal static class Fix
    {
        public static P3 XY(double x, double y) { return new P3(x, y, 0); }
        public static P3 XYH(double x, double y, double h) { return new P3(x, y, h); }

        /// <summary>Poly(0,0, 10,0, 10,10) 这种成对写法。</summary>
        public static Polyline Poly(params double[] xy)
        {
            var l = new Polyline();
            for (int i = 0; i + 1 < xy.Length; i += 2) l.Add(XY(xy[i], xy[i + 1]));
            return l;
        }

        /// <summary>a→b 之间均分 parts 段（含两端），共 parts+1 个点。</summary>
        public static Polyline Straight(P3 a, P3 b, int parts)
        {
            var l = new Polyline();
            if (parts < 1) parts = 1;
            for (int i = 0; i <= parts; i++) l.Add(P3.Lerp(a, b, (double)i / parts));
            return l;
        }

        /// <summary>折线弧长参数表：给出每个顶点的累计弧长（断言 Subspan/描边结果时用得上）。</summary>
        public static List<double> Arcs(Polyline line)
        {
            var l = new List<double>();
            if (line == null) return l;
            double acc = 0;
            for (int i = 0; i < line.Count; i++)
            {
                if (i > 0) acc += line.Points[i - 1].DistanceTo(line.Points[i]);
                l.Add(acc);
            }
            return l;
        }

        public static GraphNode Node(int id, double x, double y)
        {
            return new GraphNode { Id = id, Pos = XY(x, y), Signature = 1000 + id };
        }

        public static GraphEdge Edge(int id, NetKind kind, int startNode, int endNode, Polyline line)
        {
            return new GraphEdge
            {
                Id = id,
                Kind = kind,
                StartNode = startNode,
                EndNode = endNode,
                Line = line,
                Length = line == null ? 0 : line.Length2D(),
                Signature = 7000 + id
            };
        }

        public static BorderRef Border(int areaId, AreaTier tier, Polyline line)
        {
            return new BorderRef { AreaId = areaId, Tier = tier, Line = line, Signature = 31337 + areaId };
        }

        // ———— PlacedNode 工厂：Edge/Arc 编码规则见 TraceKit（负数=区域边界/瓦片/海岸线）————

        public static PlacedNode Free(P3 pos)
        {
            return new PlacedNode(pos, SnapKind.Free, -1, -1, 0, 0);
        }

        public static PlacedNode OnEdge(P3 pos, int edge, double arc)
        {
            return new PlacedNode(pos, SnapKind.NetCentre, -1, edge, arc, 0);
        }

        /// <summary>
        /// 路缘锚点（默认贴左侧）。老夹具里的边没有 LeftLine/RightLine，
        /// <c>GraphEdge.LineFor(side)</c> 会自动退回中心线，所以这个默认值不改变老断言的几何结果。
        /// </summary>
        public static PlacedNode OnSide(P3 pos, int edge, double arc)
        {
            return OnSide(pos, edge, arc, PlacedNode.SIDE_LEFT);
        }

        public static PlacedNode OnSide(P3 pos, int edge, double arc, byte side)
        {
            return new PlacedNode(pos, SnapKind.NetSide, -1, edge, arc, 0, side);
        }

        /// <summary>建筑轮廓锚点：Edge 用 AnchorCode.Object(o) 编码。</summary>
        public static PlacedNode OnObject(P3 pos, int objectIndex, double arc)
        {
            return new PlacedNode(pos, SnapKind.ObjectSide, -1, AnchorCode.Object(objectIndex), arc, 0);
        }

        public static PlacedNode AtNode(P3 pos, int graphNode, int edge, double arc)
        {
            return new PlacedNode(pos, SnapKind.NetNode, graphNode, edge, arc, 0);
        }

        public static PlacedNode OnBorder(P3 pos, int borderIndex, double arc)
        {
            return new PlacedNode(pos, SnapKind.AreaBorderSame, -1, -(borderIndex + 1), arc, 0);
        }

        public static TraceResult TraceOf(params P3[] pts)
        {
            var r = new TraceResult { UsedNetwork = pts != null && pts.Length > 0 };
            if (pts != null) r.Points.AddRange(pts);
            return r;
        }

        public static TraceResult TraceOfList(List<P3> pts)
        {
            var r = new TraceResult { UsedNetwork = pts != null && pts.Count > 0 };
            if (pts != null) r.Points.AddRange(pts);
            return r;
        }

        /// <summary>
        /// 造一个有 n 个手动节点、第 i 条边带 tracePoints[i] 个描边点的栈。
        /// 手动节点走 (i*100, 0) 这条直线，好让断言能一眼核对。
        /// </summary>
        public static ManualStack StackWith(int n, int[] tracePoints)
        {
            var st = new ManualStack();
            for (int i = 0; i < n; i++) st.Push(Free(XY(i * 100.0, 0)));
            for (int i = 0; i + 1 < n; i++)
            {
                int k = tracePoints == null || i >= tracePoints.Length ? 0 : Math.Max(0, tracePoints[i]);
                if (k == 0) { st.SetEdge(i, null); continue; }
                P3 a = st.Nodes[i].Pos, b = st.Nodes[i + 1].Pos;
                st.SetEdge(i, TraceOfList(StrictlyBetween(a, b, k)));
            }
            return st;
        }

        /// <summary>两点之间均分 k 个严格内部点（不含两端）。</summary>
        public static List<P3> StrictlyBetween(P3 a, P3 b, int k)
        {
            var l = new List<P3>(k);
            for (int i = 1; i <= k; i++) l.Add(P3.Lerp(a, b, (double)i / (k + 1)));
            return l;
        }

        // ———— 手搭 WorldSnapshot ————

        /// <summary>一个「边 + 两端图节点」的最小世界：一条 0→(len,0) 的直路。</summary>
        public static WorldSnapshot OneStraightEdge(double len, int parts)
        {
            var w = new WorldSnapshot();
            w.Nodes.Add(Node(0, 0, 0));
            w.Nodes.Add(Node(1, len, 0));
            w.Edges.Add(Edge(0, NetKind.Road, 0, 1, Straight(XY(0, 0), XY(len, 0), parts)));
            w.BuildAdjacency();
            return w;
        }

        /// <summary>带名字的世界构造结果，测试里用名字引用下标，避免魔法数字。</summary>
        internal sealed class Grid
        {
            public WorldSnapshot World;
            public int N0, N1, N2, N3;
            public int E0, E1, E2, E3;   // E0=底边(n0→n1) E1=右边 E2=顶边 E3=左边(n3→n0)
            public double Side;
        }

        /// <summary>
        /// 正方形环路：4 节点 4 条边，DetectRings 后每条边都该标 OnRing。
        /// bottomBulge &gt; 0 时把底边往 y 负方向拱起（弧长远大于两端直线距离），
        /// 这就是原模组 0.2.3「两点落在同一条环形路上 → 薄片段」的形状来源。
        /// </summary>
        public static Grid SquareRing(double side, double step, double bottomBulge)
        {
            var g = new Grid { Side = side };
            var w = new WorldSnapshot();
            g.N0 = 0; g.N1 = 1; g.N2 = 2; g.N3 = 3;
            w.Nodes.Add(Node(g.N0, 0, 0));
            w.Nodes.Add(Node(g.N1, side, 0));
            w.Nodes.Add(Node(g.N2, side, side));
            w.Nodes.Add(Node(g.N3, 0, side));

            Polyline bottom;
            if (bottomBulge > 0)
            {
                // 半椭圆拱：从 (0,0) 出发，中点下沉 bottomBulge，回到 (side,0)
                int parts = Math.Max(4, (int)Math.Round(side / step));
                bottom = new Polyline();
                for (int i = 0; i <= parts; i++)
                {
                    double t = (double)i / parts;
                    bottom.Add(XY(t * side, -bottomBulge * Math.Sin(Math.PI * t)));
                }
            }
            else
            {
                bottom = Straight(XY(0, 0), XY(side, 0), Math.Max(2, (int)Math.Round(side / step)));
            }
            int parts2 = Math.Max(2, (int)Math.Round(side / step));
            g.E0 = w.Edges.Count; w.Edges.Add(Edge(g.E0, NetKind.Road, g.N0, g.N1, bottom));
            g.E1 = w.Edges.Count; w.Edges.Add(Edge(g.E1, NetKind.Road, g.N1, g.N2, Straight(XY(side, 0), XY(side, side), parts2)));
            g.E2 = w.Edges.Count; w.Edges.Add(Edge(g.E2, NetKind.Road, g.N2, g.N3, Straight(XY(side, side), XY(0, side), parts2)));
            g.E3 = w.Edges.Count; w.Edges.Add(Edge(g.E3, NetKind.Road, g.N3, g.N0, Straight(XY(0, side), XY(0, 0), parts2)));
            w.BuildAdjacency();
            w.DetectRings();
            g.World = w;
            return g;
        }

        /// <summary>「L 形」两条边（n0—n1—n2），两端正交，用来验证描边沿边走而非斜穿。</summary>
        public static WorldSnapshot LShape(double arm, double step, out int n0, out int n1, out int n2, out int e0, out int e1)
        {
            var w = new WorldSnapshot();
            n0 = 0; n1 = 1; n2 = 2;
            w.Nodes.Add(Node(n0, 0, 0));
            w.Nodes.Add(Node(n1, arm, 0));
            w.Nodes.Add(Node(n2, arm, arm));
            int parts = Math.Max(2, (int)Math.Round(arm / step));
            e0 = 0; w.Edges.Add(Edge(e0, NetKind.Road, n0, n1, Straight(XY(0, 0), XY(arm, 0), parts)));
            e1 = 1; w.Edges.Add(Edge(e1, NetKind.Rail, n1, n2, Straight(XY(arm, 0), XY(arm, arm), parts)));
            w.BuildAdjacency();
            return w;
        }

        /// <summary>
        /// 贴合测试用的世界（坐标全是整数，断言里的分数都能手算核对）：
        ///  中心线：n0(0,0) —e0— n4(50,0) —e1— n1(100,0) —e2— n2(200,0)，每条边另带 y=±6 的两条路缘线（路宽 12）；
        ///  支路：e3 n1(100,0) → n3(100,40)（步行道，无路缘线）；
        ///  ⇒ n1 度数 3（真交叉口）、n4 度数 2（同一条路的分段点）、n0/n2/n3 度数 1（断头）。
        ///  区域边界 b0：x=180 的竖线，Tier=District。
        /// </summary>
        internal sealed class SnapWorld
        {
            public WorldSnapshot World;
            public int E0, E1, E2, E3;            // 中心线边下标
            public int NDeadWest, NJunction, NEastEnd, NBranch, NMid;   // 图节点下标
            public int BorderDistrict;            // world.Borders 里那条 District 边界的下标
        }

        public static SnapWorld SnapFixture()
        {
            var s = new SnapWorld();
            var w = new WorldSnapshot();
            s.NDeadWest = 0; s.NJunction = 1; s.NEastEnd = 2; s.NBranch = 3; s.NMid = 4;
            w.Nodes.Add(Node(0, 0, 0));
            w.Nodes.Add(Node(1, 100, 0));
            w.Nodes.Add(Node(2, 200, 0));
            w.Nodes.Add(Node(3, 100, 40));
            w.Nodes.Add(Node(4, 50, 0));

            s.E0 = w.Edges.Count;
            var e0 = Edge(s.E0, NetKind.Road, s.NDeadWest, s.NMid, Straight(XY(0, 0), XY(50, 0), 5));
            e0.RightLine = Straight(XY(0, -6), XY(50, -6), 5);
            e0.LeftLine = Straight(XY(0, 6), XY(50, 6), 5);
            e0.RoadWidth = 12;
            w.Edges.Add(e0);
            s.E1 = w.Edges.Count;
            var e1 = Edge(s.E1, NetKind.Road, s.NMid, s.NJunction, Straight(XY(50, 0), XY(100, 0), 5));
            e1.RightLine = Straight(XY(50, -6), XY(100, -6), 5);
            e1.LeftLine = Straight(XY(50, 6), XY(100, 6), 5);
            e1.RoadWidth = 12;
            w.Edges.Add(e1);
            s.E2 = w.Edges.Count;
            var e2 = Edge(s.E2, NetKind.Road, s.NJunction, s.NEastEnd, Straight(XY(100, 0), XY(200, 0), 10));
            e2.RightLine = Straight(XY(100, -6), XY(200, -6), 10);
            e2.LeftLine = Straight(XY(100, 6), XY(200, 6), 10);
            e2.RoadWidth = 12;
            w.Edges.Add(e2);
            s.E3 = w.Edges.Count;
            w.Edges.Add(Edge(s.E3, NetKind.Path, s.NJunction, s.NBranch, Straight(XY(100, 0), XY(100, 40), 4)));

            s.BorderDistrict = w.Borders.Count;
            w.Borders.Add(Border(500, AreaTier.District, Poly(180, -100, 180, 0, 180, 100)));
            w.BuildAdjacency();
            s.World = w;
            return s;
        }

        /// <summary>
        /// 手搭 ResolvedTuning：把「算法输入」与「界面策略解析」解耦，断言只钉算法。
        ///
        /// ⚠ 旋钮必须显式填（第五轮反馈 5 之后）：描边现在读 <see cref="ResolvedTuning.Knobs"/> 里的
        /// PreferOutermost / AllowNetworkSwitch / 转弯与换类代价，也读 CornerDegrees 决定「哪里算交点、要不要钉一个节点」。
        /// 结构体默认值会让这些全是 0/false ⇒ 夹具就跑成「不切换网络、不搜外围、处处不是交点」，
        /// 那已经不是实机那条链路了。这里统一按**默认模式 Smart** 填，与 PolicyKit.Resolve 的默认输出口径一致；
        /// 要测别的模式就调 <see cref="TunMode"/>。
        /// </summary>
        public static ResolvedTuning Tun(AreaTier tier, double snapRadius, double tol, int maxNodes, double minSpacing, double detour)
        {
            ResolvedTuning r = new ResolvedTuning
            {
                Tier = tier,
                SnapRadius = snapRadius,
                SimplifyTolerance = tol,
                MaxNodesPerEdge = maxNodes,
                MinSpacing = minSpacing,
                MaxDetourFactor = detour,
                SnapEnabled = true,
                TraceEnabled = true,
                ContinuationEnabled = true,      // 反馈 7：默认开，夹具跟着默认口径走
                Mode = TraceMode.Smart,
            };
            r.Knobs = PolicyKit.ModeKnobs(TraceMode.Smart);
            r.Knobs.MaxDetourFactor = detour;    // 绕行上限由参数给（有的夹具要卡它）
            r.CornerDegrees = r.Knobs.CornerDegrees;
            return r;
        }

        /// <summary>同一组数，但换一套模式旋钮（测「贴合模式」对选路的影响时用这个）。</summary>
        public static ResolvedTuning TunMode(AreaTier tier, double snapRadius, double tol, int maxNodes,
                                             double minSpacing, double detour, TraceMode mode)
        {
            ResolvedTuning r = Tun(tier, snapRadius, tol, maxNodes, minSpacing, detour);
            r.Mode = mode;
            r.Knobs = PolicyKit.ModeKnobs(mode);
            r.Knobs.MaxDetourFactor = detour;
            r.MaxDetourFactor = Math.Max(1.0, r.Knobs.MaxDetourFactor);
            r.CornerDegrees = r.Knobs.CornerDegrees;
            return r;
        }

        /// <summary>同一组数，但把拐点判定改掉（0 = 处处不算交点 ⇒ 不钉任何点；用于隔离「钉点」这一条的影响）。</summary>
        public static ResolvedTuning TunCorner(AreaTier tier, double cornerDegrees)
        {
            ResolvedTuning r = TraceTuning(tier);
            r.CornerDegrees = cornerDegrees;
            return r;
        }

        /// <summary>描边常用的一组数：半径 60、容差 0.5（几乎不简化）、预算 60 点、间距 2、绕行 3 倍。</summary>
        public static ResolvedTuning TraceTuning(AreaTier tier)
        {
            return Tun(tier, 60, 0.5, 60, 2.0, 3.0);
        }

        public static TraceContext EmptyCtx()
        {
            return new TraceContext();
        }
    }
}
