using System;
using System.Collections.Generic;
using ZoneSnapper.Engine;

namespace ZoneSnapper.T3
{
    /// <summary>
    /// 需求 2 + 3 + 4 的新语义：贴合目标按区域类别分档、外沿/中心线各自描边、
    /// 跨越判定、最外围选路、类型变化处强制放节点、弯道弦高密度。
    ///
    /// 这一节存在的理由很直接：v0.1.0/v0.1.1 的「贴路缘」其实是贴中心线（游戏侧把
    /// (m_Left.a + m_Right.a)/2 当偏移量，那个中点就是中心线本身），离线壳里没有任何一条
    /// 断言钉住「产业区贴的点必须不在中心线上」，所以两轮都全绿、实机两轮都不对。
    /// 现在把这些形状逐条钉住。
    /// </summary>
    internal static partial class Tests
    {
        internal static class Targets
        {
            /// <summary>一条东西向的路：中心线 y=0，左缘 y=+6，右缘 y=-6，路宽 12。</summary>
            private static WorldSnapshot Road(bool tunnel, out int edgeIdx)
            {
                var w = new WorldSnapshot();
                w.Nodes.Add(Fix.Node(0, 0, 0));
                w.Nodes.Add(Fix.Node(1, 100, 0));
                var e = Fix.Edge(0, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(0, 0), Fix.XY(100, 0), 10));
                e.LeftLine = Fix.Straight(Fix.XY(0, 6), Fix.XY(100, 6), 10);
                e.RightLine = Fix.Straight(Fix.XY(0, -6), Fix.XY(100, -6), 10);
                e.RoadWidth = 12;
                e.Tunnel = tunnel;
                edgeIdx = 0;
                w.Edges.Add(e);
                w.BuildAdjacency();
                return w;
            }

            public static void Run()
            {
                var cfg = ModConfig.CreateDefault();

                // —————————————————————————————— 侧向：同一条路的两条外沿选哪一条
                Harness.Section("SnapKit 外沿定侧（需求 3：贴路缘 + 不许把马路划进地块）（X1）", () =>
                {
                    int ei;
                    var w = Road(false, out ei);
                    var lot = PolicyKit.Resolve(cfg, AreaTier.Lot, 20);      // 半径 20×1.5 = 30
                    var raw = Fix.XY(50, -0.5);                               // 离右缘 5.5、离左缘 6.5

                    var plain = SnapKit.FindSnap(raw, w, cfg, lot, Fix.Free(raw));
                    Check.Enum("X1 没有质心 ⇒ 贴更近的那条缘", SnapKind.NetSide, plain.Kind);
                    Check.Bool("X1 更近的右缘胜出", true, plain.Side == PlacedNode.SIDE_RIGHT);
                    Check.Close("X1 落点在右缘上（y=-6，不是中心线 y=0）", -6, plain.Pos.Y, 1e-9);

                    var north = new SnapKit.SnapExtra { HasCentroid = true, Centroid = Fix.XY(50, 30) };
                    var up = SnapKit.FindSnap(raw, w, cfg, lot, Fix.Free(raw), north);
                    Check.Bool("X1 区域在路北 ⇒ 宁可多走 1 米也要贴北缘（北缘才不把马路划进地块）",
                        true, up.Side == PlacedNode.SIDE_LEFT);
                    Check.Close("X1 质心判侧改变了落点", 6, up.Pos.Y, 1e-9);

                    var south = new SnapKit.SnapExtra { HasCentroid = true, Centroid = Fix.XY(50, -30) };
                    var down = SnapKit.FindSnap(raw, w, cfg, lot, Fix.Free(raw), south);
                    Check.Bool("X1 区域在路南 ⇒ 贴南缘（对称）", true, down.Side == PlacedNode.SIDE_RIGHT);

                    // 侧向粘性只在「两侧等距」时起作用：不该反过来覆盖玩家明显更近的那一侧。
                    // 用 Surface 档而不是 Lot：Lot 的严格跨越闸会把对侧缘候选直接拒掉（那是 X2 的事），
                    // 这里要测的是「两个候选都在桌上时谁赢」，不能被另一道闸替它做决定。
                    var surfSticky = PolicyKit.Resolve(cfg, AreaTier.Surface, 20);
                    var mid = Fix.XY(50, 0);
                    var noStick = SnapKit.FindSnap(mid, w, cfg, surfSticky, Fix.Free(mid));
                    Check.Bool("X1 两侧等距 ⇒ 保留扫描顺序里的先来者（左缘）",
                        true, noStick.Kind == SnapKind.NetSide && noStick.Side == PlacedNode.SIDE_LEFT);
                    var stuck = new SnapKit.SnapExtra
                    {
                        HasPrevious = true,
                        Previous = Fix.OnSide(Fix.XY(20, -6), 0, 20, PlacedNode.SIDE_RIGHT)
                    };
                    var withStick = SnapKit.FindSnap(mid, w, cfg, surfSticky, Fix.Free(mid), stuck);
                    Check.Bool("X1 上一格贴在右缘 ⇒ 这一格也走右缘（同一条路上不来回翻）",
                        true, withStick.Kind == SnapKind.NetSide && withStick.Side == PlacedNode.SIDE_RIGHT);
                    var nearWins = SnapKit.FindSnap(Fix.XY(50, 5.5), w, cfg, surfSticky,
                        Fix.Free(Fix.XY(50, 5.5)), stuck);
                    Check.Bool("X1 粘性压不过明显更近的一侧（玩家点在北缘上就该贴北缘）",
                        true, nearWins.Side == PlacedNode.SIDE_LEFT);

                    // 同一段同侧 ⇒ 两次调用逐字段全等（需求 4：两边区域的节点要完全吻合的前提）
                    var p1 = SnapKit.FindSnap(Fix.XY(70, -6), w, cfg, lot, Fix.Free(Fix.XY(70, -6)));
                    var p2 = SnapKit.FindSnap(Fix.XY(70, -6), w, cfg, lot, Fix.Free(Fix.XY(70, -6)));
                    Check.True("X1 同输入两次调用逐字段全等（含 Side）", () =>
                        p1.Kind == p2.Kind && p1.Edge == p2.Edge && p1.Side == p2.Side
                        && p1.Pos.X == p2.Pos.X && p1.Pos.Y == p2.Pos.Y && p1.Arc == p2.Arc);
                });

                // —————————————————————————————— 跨越：Lot 不许、Surface 只保留直线
                Harness.Section("SnapKit/TraceKit 跨越道路与建筑（需求 3 的第二半）（X2）", () =>
                {
                    int ei;
                    var w = Road(false, out ei);
                    var lot = PolicyKit.Resolve(cfg, AreaTier.Lot, 20);
                    var surf = PolicyKit.Resolve(cfg, AreaTier.Surface, 20);
                    var prev = Fix.OnSide(Fix.XY(50, -6), 0, 50, PlacedNode.SIDE_RIGHT);
                    var raw = Fix.XY(60, 5);          // 马路对面那一侧的缘离它只有 1 米

                    var lotExtra = new SnapKit.SnapExtra { HasPrevious = true, Previous = prev };
                    var strict = SnapKit.FindSnap(raw, w, cfg, lot, Fix.Free(raw), lotExtra);
                    Check.True("X2 产业区：对侧缘那个更近的候选被拒（不许跨越马路）", () =>
                        strict.Kind == SnapKind.NetSide && strict.Side == PlacedNode.SIDE_RIGHT);

                    var loose = SnapKit.FindSnap(raw, w, cfg, surf, Fix.Free(raw), lotExtra);
                    Check.True("X2 表面类：允许吸到对侧（差别就在 BlocksCrossing 这一档）", () =>
                        loose.Kind == SnapKind.NetSide && loose.Side == PlacedNode.SIDE_LEFT);

                    var cross = TraceKit.Trace(prev, loose, w, cfg, surf, Fix.EmptyCtx());
                    Check.Enum("X2 表面类跨了马路的那一段：不贴合，保留玩家自己的直线",
                        TraceFallback.CrossesObstacle, cross.Fallback);
                    Check.Int("X2 回退时一个点都不生成（否则等于偷偷把马路圈进去）", 0, cross.Points.Count);

                    var sameSide = Fix.OnSide(Fix.XY(80, -6), 0, 80, PlacedNode.SIDE_RIGHT);
                    var ok = TraceKit.Trace(prev, sameSide, w, cfg, surf, Fix.EmptyCtx());
                    Check.True("X2 同侧相邻两点照常描边（跨越闸没有误伤）", () =>
                        ok.Fallback == TraceFallback.None && ok.UsedNetwork);
                    Check.True("X2 同侧描边的点全在右缘上", () =>
                    {
                        for (int i = 0; i < ok.Points.Count; i++) if (Math.Abs(ok.Points[i].Y + 6) > 1e-9) return false;
                        return true;
                    });

                    // CrossesObstacle 的三个放行条件逐个钉住（每一个都对应一种实机会画出来的形状）
                    // 注：这里一律写成 () => 形式。Check.False 只有收 Func<bool> 的重载，
                    // 传裸 bool 会编译失败；而且 lambda 里的求值异常会被记成这一条的 FAIL，不会把整个壳崩掉。
                    Check.False("X2 沿着右缘走一整条路不算跨越", () => SnapKit.CrossesObstacle(w, Fix.XY(0, -6), Fix.XY(100, -6)));
                    Check.True("X2 从南缘拉到北缘 = 横穿马路", () => SnapKit.CrossesObstacle(w, Fix.XY(40, -6), Fix.XY(60, 6)));
                    Check.False("X2 两端同在中心线一侧不算跨越", () => SnapKit.CrossesObstacle(w, Fix.XY(10, -6), Fix.XY(90, -2)));

                    var junction = new WorldSnapshot();
                    junction.Nodes.Add(Fix.Node(0, 0, 0));
                    junction.Nodes.Add(Fix.Node(1, 100, 0));
                    junction.Nodes.Add(Fix.Node(2, 100, 60));
                    junction.Edges.Add(Fix.Edge(0, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(0, 0), Fix.XY(100, 0), 10)));
                    junction.Edges.Add(Fix.Edge(1, NetKind.Road, 1, 2, Fix.Straight(Fix.XY(100, 0), Fix.XY(100, 60), 6)));
                    junction.BuildAdjacency();
                    Check.False("X2 L 形路口拐过去不算跨越（交点在这条路的端头 12 米内）",
                        () => SnapKit.CrossesObstacle(junction, Fix.XY(100, -6), Fix.XY(94, 20)));
                    Check.True("X2 但横穿这条主路的中段仍然算",
                        () => SnapKit.CrossesObstacle(junction, Fix.XY(60, -6), Fix.XY(100, 30)));

                    var bw = Road(false, out ei);
                    bw.Objects.Add(new ObjectRef
                    {
                        Id = 0,
                        OwnerIndex = 0,
                        Line = Fix.Poly(40, -20, 60, -20, 60, -10, 40, -10, 40, -20),
                        Signature = 99,
                        IsBuilding = true          // 反馈 9 之后轮廓档只认建筑，夹具得说清它是一栋楼
                    });
                    Check.True("X2 穿过一栋楼的轮廓算跨越（需求 3：产业区不许跨建筑）",
                        () => SnapKit.CrossesObstacle(bw, Fix.XY(50, -25), Fix.XY(50, -5)));
                    Check.False("X2 绕着楼走不算",
                        () => SnapKit.CrossesObstacle(bw, Fix.XY(30, -25), Fix.XY(30, -5)));
                });

                // —————————————————————————————— 描边沿哪条折线
                Harness.Section("TraceKit 描边沿类别对应的那条线（需求 3 的落点）（X3）", () =>
                {
                    var w = new WorldSnapshot();
                    w.Nodes.Add(Fix.Node(0, 0, 0));
                    w.Nodes.Add(Fix.Node(1, 100, 0));
                    var e = Fix.Edge(0, NetKind.Road, 0, 1, Fix.Poly(0, 0, 50, 10, 100, 0));
                    e.LeftLine = Fix.Poly(0, 6, 50, 16, 100, 6);
                    e.RightLine = Fix.Poly(0, -6, 50, 4, 100, -6);
                    e.RoadWidth = 12;
                    w.Edges.Add(e);
                    w.BuildAdjacency();
                    double centreLen = e.Line.Length2D();
                    double sideLen = e.RightLine.Length2D();

                    var lotT = Fix.Tun(AreaTier.Lot, 60, 0.5, 60, 2.0, 3.0);
                    var a1 = Fix.OnSide(Fix.XY(0, -6), 0, 0, PlacedNode.SIDE_RIGHT);
                    var b1 = Fix.OnSide(Fix.XY(100, -6), 0, sideLen, PlacedNode.SIDE_RIGHT);
                    var side = TraceKit.Trace(a1, b1, w, cfg, lotT, Fix.EmptyCtx());
                    Check.True("X3 地块描边走的是右缘（拐角取在 y=4，不是中心线的 y=10）", () =>
                        HasNear(side.Points, Fix.XY(50, 4)));
                    Check.True("X3 结果里没有任何中心线/左缘上的点", () =>
                    {
                        for (int i = 0; i < side.Points.Count; i++)
                        {
                            if (Math.Abs(side.Points[i].Y - 10) < 1e-6) return false;
                            if (Math.Abs(side.Points[i].Y - 16) < 1e-6) return false;
                        }
                        return true;
                    });

                    var disT = Fix.Tun(AreaTier.District, 60, 0.5, 60, 2.0, 3.0);
                    var a2 = Fix.OnEdge(Fix.XY(0, 0), 0, 0);
                    var b2 = Fix.OnEdge(Fix.XY(100, 0), 0, centreLen);
                    var mid = TraceKit.Trace(a2, b2, w, cfg, disT, Fix.EmptyCtx());
                    Check.True("X3 市辖区描边仍然沿中心线（两档各走各的线，互不串）", () =>
                        HasNear(mid.Points, Fix.XY(50, 10)));

                    // 一端是路口：跟着有侧的那一端走，而不是退回中心线
                    var a3 = Fix.AtNode(Fix.XY(0, 0), 0, 0, 0);
                    var mix = TraceKit.Trace(a3, b1, w, cfg, lotT, Fix.EmptyCtx());
                    Check.True("X3 路口 + 右缘 ⇒ 整条走右缘", () =>
                        mix.Fallback == TraceFallback.None && HasNear(mix.Points, Fix.XY(50, 4)));

                    // 需求 3 的第一句「首尾两点有一个不在道路上 ⇒ 不自动贴合」在第八轮改了写法：
                    // 玩家那一格不许被挪（反馈 3/10），但走线要**自己长出接入点**贴上去（反馈 2/6/8）——
                    // 于是"不贴合"只剩一种情形：那一格附近本来就没有这一档该贴的东西（X3 之外由 T7/T8 钉）。
                    // 这里要钉的是另一半：路缘档长出来的那个点**只许落在缘线上，不许落在马路中间**。
                    var free = Fix.Free(Fix.XY(50, -40));
                    var none = TraceKit.Trace(a1, free, w, cfg, lotT, Fix.EmptyCtx());
                    Check.True("X3 地块：自由端长出接入点，走线照样贴（反馈 8「产业区几乎看不到贴合路径」）", () =>
                        none.UsedNetwork && none.Attached >= 1 && none.Fallback == TraceFallback.None);
                    Check.True("X3 接入点落在马路**南缘**上，不是马路中间（反馈 8 第二句）", () =>
                    {
                        if (none.Points.Count == 0) return false;
                        bool onRight = false;
                        for (int i = 0; i < none.Points.Count; i++)
                        {
                            P3 p = none.Points[i];
                            if (SnapKit.SignedSide(e.RightLine, p) == 0) { onRight = true; continue; }
                            if (SnapKit.SignedSide(e.LeftLine, p) == 0) continue;          // 马路另一侧：下面那条断言管
                            return false;                                                   // 既不在缘上 ⇒ 压在马路中间/街区里
                        }
                        return onRight;
                    });

                    // 一端是**旧数据/游戏自己递来的中心线锚点**：路缘档不认它（IsTraceable 那一层），
                    // 于是它被重挂到缘线上 —— 走线里一个中心线上的点都不许有。
                    var wrongKind = Fix.OnEdge(Fix.XY(100, 0), 0, centreLen);
                    var notMine = TraceKit.Trace(a1, wrongKind, w, cfg, lotT, Fix.EmptyCtx());
                    Check.True("X3 地块的一端吸在中心线上 ⇒ 重挂到缘线后再描边（不视为中心线档）", () =>
                        notMine.Fallback == TraceFallback.None && notMine.UsedNetwork);
                    Check.True("X3 重挂之后的走线上没有任何中心线/左缘上的点", () =>
                    {
                        for (int i = 0; i < notMine.Points.Count; i++)
                        {
                            P3 p = notMine.Points[i];
                            if (SnapKit.SignedSide(e.Line, p) == 0) return false;        // 压在中心线上
                            if (SnapKit.SignedSide(e.LeftLine, p) == 0) return false;    // 跑到马路另一侧
                        }
                        return true;
                    });
                    Check.True("X3 拐角取在右缘的 (50,4)，不是中心线的 (50,10)", () =>
                        HasNear(notMine.Points, Fix.XY(50, 4)));
                });

                // —————————————————————————————— 最外围选路
                Harness.Section("TraceKit 最外围路段（需求 2：不沿中间小路贴合、也不绕大圈）（X4）", () =>
                {
                    //   n0(0,0) ── 小路 ── n1(100,0)          ← 穿街区的那条
                    //     │                     │
                    //   nS1(0,-100) ── 外围 ── nS2(100,-100)   ← 街区南边的马路
                    // 两端点 A(0,-30) 在西侧路上、B(100,-30) 在东侧路上：两条路线同样 240 / 160 米。
                    var w = new WorldSnapshot();
                    w.Nodes.Add(Fix.Node(0, 0, 0));          // n0
                    w.Nodes.Add(Fix.Node(1, 100, 0));         // n1
                    w.Nodes.Add(Fix.Node(2, 0, -100));        // nS1
                    w.Nodes.Add(Fix.Node(3, 100, -100));      // nS2
                    w.Edges.Add(Fix.Edge(0, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(0, 0), Fix.XY(100, 0), 2)));
                    w.Edges.Add(Fix.Edge(1, NetKind.Road, 0, 2, Fix.Straight(Fix.XY(0, 0), Fix.XY(0, -100), 2)));
                    w.Edges.Add(Fix.Edge(2, NetKind.Road, 2, 3, Fix.Straight(Fix.XY(0, -100), Fix.XY(100, -100), 2)));
                    w.Edges.Add(Fix.Edge(3, NetKind.Road, 3, 1, Fix.Straight(Fix.XY(100, -100), Fix.XY(100, 0), 2)));
                    w.BuildAdjacency();

                    var tuning = Fix.Tun(AreaTier.District, 60, 0.5, 60, 2.0, 3.0);
                    var a = Fix.OnEdge(Fix.XY(0, -30), 1, 30);
                    var b = Fix.OnEdge(Fix.XY(100, -30), 3, 70);

                    var noCtx = TraceKit.Trace(a, b, w, cfg, tuning, Fix.EmptyCtx());
                    Check.True("X4 没有质心（区域还没成形）⇒ 用最短路：走穿街区的小路", () =>
                        noCtx.Fallback == TraceFallback.None && HasNear(noCtx.Points, Fix.XY(0, 0)));

                    var ctx = new TraceContext
                    {
                        HasCentroid = true,
                        Centroid = Fix.XY(50, 100)            // 区域往北铺开 ⇒ 南侧的外围路才是「外围」
                    };
                    var outer = TraceKit.Trace(a, b, w, cfg, tuning, ctx);
                    Check.True("X4 有质心 ⇒ 换到最外围那条路（南边的马路），不再沿中间小路贴", () =>
                        outer.Fallback == TraceFallback.None && HasNear(outer.Points, Fix.XY(0, -100))
                        && HasNear(outer.Points, Fix.XY(100, -100)));
                    Check.True("X4 外围那条路里不再出现小路上的点", () =>
                    {
                        for (int i = 0; i < outer.Points.Count; i++)
                        {
                            if (Math.Abs(outer.Points[i].Y) < 1e-6 && outer.Points[i].X > 0 && outer.Points[i].X < 100) return false;
                        }
                        return true;
                    });
                    Check.Close("X4 走线代价 = 240 米（绕行比按纯几何长度算，惩罚项不参与）", 240, outer.Cost, 1e-6);
                    Check.True("X4 同输入两次搜出的路线完全一致（确定性）", () =>
                    {
                        var again = TraceKit.Trace(a, b, w, cfg, tuning, ctx);
                        if (again.Points.Count != outer.Points.Count) return false;
                        for (int i = 0; i < outer.Points.Count; i++)
                        {
                            if (again.Points[i].X != outer.Points[i].X || again.Points[i].Y != outer.Points[i].Y) return false;
                        }
                        return true;
                    });

                    // 需求 4 的后半：路上有重叠物体不能把市辖区的描边挤到另一条路径上
                    var crowded = new WorldSnapshot();
                    for (int i = 0; i < w.Nodes.Count; i++) crowded.Nodes.Add(Fix.Node(w.Nodes[i].Id, w.Nodes[i].Pos.X, w.Nodes[i].Pos.Y));
                    for (int i = 0; i < w.Edges.Count; i++)
                    {
                        var ge = Fix.Edge(w.Edges[i].Id, w.Edges[i].Kind, w.Edges[i].StartNode, w.Edges[i].EndNode, w.Edges[i].Line);
                        ge.RoadWidth = w.Edges[i].RoadWidth;
                        crowded.Edges.Add(ge);
                    }
                    crowded.BuildAdjacency();
                    crowded.Objects.Add(new ObjectRef
                    {
                        Id = 0, OwnerIndex = 0, Signature = 5, IsBuilding = true,
                        Line = Fix.Poly(40, -10, 60, -10, 60, 10, 40, 10, 40, -10)      // 正好压在小路中段上
                    });
                    var withObjects = TraceKit.Trace(a, b, crowded, cfg, Fix.Tun(AreaTier.District, 60, 0.5, 60, 2.0, 3.0), ctx);
                    Check.True("X4 中心线档不看建筑轮廓 ⇒ 有物体压在路上也搜出同一条路线", () =>
                    {
                        var plain = TraceKit.Trace(a, b, w, cfg, Fix.Tun(AreaTier.District, 60, 0.5, 60, 2.0, 3.0), ctx);
                        if (withObjects.Points.Count != plain.Points.Count) return false;
                        for (int i = 0; i < plain.Points.Count; i++)
                        {
                            if (withObjects.Points[i].DistanceTo(plain.Points[i]) > 1e-9) return false;
                        }
                        return true;
                    });
                });

                // —————————————————————————————— 类型变化处必须留节点
                Harness.Section("TraceKit 网络类型变化处的强制节点（需求 2）（X5）", () =>
                {
                    // 三段**完全共线**的路：中间那格是铁路。RDP / 合共线 / 去近邻三道闸都会把
                    // 「道路→铁路」那个角当成没用的共线点删掉，删掉之后边界直接穿过道岔。
                    var w = new WorldSnapshot();
                    w.Nodes.Add(Fix.Node(0, 0, 0));
                    w.Nodes.Add(Fix.Node(1, 100, 0));
                    w.Nodes.Add(Fix.Node(2, 200, 0));
                    var e0 = Fix.Edge(0, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(0, 0), Fix.XY(100, 0), 2));
                    var e1 = Fix.Edge(1, NetKind.Rail, 1, 2, Fix.Straight(Fix.XY(100, 0), Fix.XY(200, 0), 2));
                    w.Edges.Add(e0);
                    w.Edges.Add(e1);
                    w.BuildAdjacency();

                    var tuning = Fix.Tun(AreaTier.District, 60, 0.5, 60, 2.0, 3.0);
                    // Arc 是「本条边自己的弧长」（0..100），不是全局弧长：写成 200 会让
                    // AnchorToNodeCost 凭空多出 100 米代价，测的就不是共线保角而是别的东西了。
                    var a = Fix.OnEdge(Fix.XY(0, 0), 0, 0);
                    var b = Fix.OnEdge(Fix.XY(200, 0), 1, 100);
                    var r = TraceKit.Trace(a, b, w, cfg, tuning, Fix.EmptyCtx());
                    Check.True("X5 道路→铁路的交叉点在共线情况下仍被保留", () =>
                        r.Fallback == TraceFallback.None && HasNear(r.Points, Fix.XY(100, 0)));
                    Check.Int("X5 走线本身没错（两段接入拼起来 200 米）", 200, (int)System.Math.Round(r.Cost));
                    Check.Int("X5 换类接头只登记一个钉（重复登记会让 Dedupe 白忙一场）", 1, r.Pins.Count);

                    // Kind 同为 Road，只有游戏自己的 Layer 位变了（普通道路 ↔ 公共交通道路）也要留
                    var w2 = new WorldSnapshot();
                    w2.Nodes.Add(Fix.Node(0, 0, 0));
                    w2.Nodes.Add(Fix.Node(1, 100, 0));
                    w2.Nodes.Add(Fix.Node(2, 200, 0));
                    var g0 = Fix.Edge(0, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(0, 0), Fix.XY(100, 0), 2));
                    var g1 = Fix.Edge(1, NetKind.Road, 1, 2, Fix.Straight(Fix.XY(100, 0), Fix.XY(200, 0), 2));
                    g0.LayerBits = 0x1;        // Layer.Road
                    g1.LayerBits = 0x8000;     // Layer.PublicTransportRoad
                    w2.Edges.Add(g0);
                    w2.Edges.Add(g1);
                    w2.BuildAdjacency();
                    var r2 = TraceKit.Trace(a, b, w2, cfg, tuning, Fix.EmptyCtx());
                    Check.True("X5 只有 Layer 位变了也算类型变化", () => HasNear(r2.Points, Fix.XY(100, 0)));

                    // 全程同类 ⇒ 共线的中间点该删就删（钉节点不能变成「一路铺点」）
                    var w3 = new WorldSnapshot();
                    w3.Nodes.Add(Fix.Node(0, 0, 0));
                    w3.Nodes.Add(Fix.Node(1, 100, 0));
                    w3.Nodes.Add(Fix.Node(2, 200, 0));
                    var h0 = Fix.Edge(0, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(0, 0), Fix.XY(100, 0), 2));
                    var h1 = Fix.Edge(1, NetKind.Road, 1, 2, Fix.Straight(Fix.XY(100, 0), Fix.XY(200, 0), 2));
                    h0.LayerBits = 0x1;
                    h1.LayerBits = 0x1;
                    w3.Edges.Add(h0);
                    w3.Edges.Add(h1);
                    w3.BuildAdjacency();
                    var r3 = TraceKit.Trace(a, b, w3, cfg, tuning, Fix.EmptyCtx());
                    Check.True("X5 全程同类型 ⇒ 中间的共线点照删（需求 2：直线路段正常 2 个节点）", () =>
                        !HasNear(r3.Points, Fix.XY(100, 0)));
                    Check.Int("X5 全程同类型 ⇒ 一个钉都不登记（钉不是「每个接头都留」）", 0, r3.Pins.Count);
                });

                // —————————————————————————————— 建筑轮廓档
                Harness.Section("SnapKit/TraceKit 同一建筑边缘档（需求 3）（X6）", () =>
                {
                    var w = new WorldSnapshot();
                    w.Objects.Add(new ObjectRef
                    {
                        Id = 0, OwnerIndex = 7, Signature = 4242, IsBuilding = true,
                        Line = Fix.Poly(0, 0, 20, 0, 20, 20, 0, 20, 0, 0)
                    });
                    var lot = PolicyKit.Resolve(cfg, AreaTier.Lot, 20);
                    var raw = Fix.XY(10, 0.5);
                    var r = SnapKit.FindSnap(raw, w, cfg, lot, Fix.Free(raw));
                    Check.Enum("X6 地块类可以贴建筑轮廓", SnapKind.ObjectSide, r.Kind);
                    Check.True("X6 轮廓锚点用 -(3e6+o) 编码（与海岸/边界档不冲突）",
                        () => r.Edge == AnchorCode.Object(0) && AnchorCode.IsObject(r.Edge));
                    Check.Close("X6 落点压在这条楼边上", 0, r.Pos.Y, 1e-9);

                    // 反馈 9：只有**建筑**轮廓是目标。同一位置换成一个摆件（树 / 高架桥墩 / 码头桩），
                    // 就不许吸上去 —— 游戏原版那一档对任何非圆形静态物体都贴边，这是本模组收紧的地方。
                    var propWorld = new WorldSnapshot();
                    propWorld.Objects.Add(new ObjectRef
                    {
                        Id = 0, OwnerIndex = 7, Signature = 4243, IsBuilding = false,
                        Line = Fix.Poly(0, 0, 20, 0, 20, 20, 0, 20, 0, 0)
                    });
                    var rp = SnapKit.FindSnap(raw, propWorld, cfg, lot, Fix.Free(raw));
                    Check.Enum("X6 摆件轮廓不是贴合目标（反馈 9：只限建筑边缘）", SnapKind.Free, rp.Kind);
                    Check.Bool("X6 策略层也认这条：ObjectAllowed 只对建筑放行", false,
                        PolicyKit.ObjectAllowed(AreaTier.Lot, propWorld.Objects[0]));

                    var dis = PolicyKit.Resolve(cfg, AreaTier.District, 20);
                    var rd = SnapKit.FindSnap(raw, w, cfg, dis, Fix.Free(raw));
                    Check.Enum("X6 市辖区不贴建筑（游戏给 District 的只有 NetMiddle）", SnapKind.Free, rd.Kind);

                    var a = Fix.OnObject(Fix.XY(10, 0), 0, 10);
                    var b = Fix.OnObject(Fix.XY(20, 10), 0, 30);
                    var tr = TraceKit.Trace(a, b, w, cfg, Fix.Tun(AreaTier.Lot, 60, 0.5, 60, 2.0, 3.0), Fix.EmptyCtx());
                    Check.True("X6 同一栋楼的两点 ⇒ 沿楼边走过拐角 (20,0)", () =>
                        tr.Fallback == TraceFallback.None && HasNear(tr.Points, Fix.XY(20, 0)));
                });

                // —————————————————————————————— 弦高与密度
                Harness.Section("PolicyKit/GeoKit 弦高容差与曲率密度（需求 2/3）（X7）", () =>
                {
                    // 反馈 7 之后容差的算法是「路宽 × 比例，再夹到该档区间」，比例由曲线精细度插值：
                    // 精细度 0（默认）时 District 25%/[1,6]、Lot 8%/[0.3,1.2]、Surface 10%/[0.25,1.5]。
                    Check.Close("X7 市辖区拿不到路宽 ⇒ 按最窄路 3 米算，并被该档下限抬到 1 米",
                        1.0, PolicyKit.ChordTolerance(cfg, AreaTier.District, 0), 1e-12);
                    Check.Close("X7 市辖区 20 米路 ⇒ 20×25%=5 米（默认档就是「只在交点放点」，允许轻微切角）",
                        5.0, PolicyKit.ChordTolerance(cfg, AreaTier.District, 20), 1e-12);
                    Check.Close("X7 产业区 20 米路 ⇒ 夹到该档上限 1.2 米（切出去就是地块吃了人行道）",
                        1.2, PolicyKit.ChordTolerance(cfg, AreaTier.Lot, 20), 1e-12);
                    Check.Close("X7 产业区拿不到路宽 ⇒ 按 3 米算，夹到下限 0.3",
                        0.3, PolicyKit.ChordTolerance(cfg, AreaTier.Lot, 0), 1e-12);
                    Check.True("X7 越靠路缘档容差越紧", PolicyKit.ChordTolerance(cfg, AreaTier.Lot, 20)
                        < PolicyKit.ChordTolerance(cfg, AreaTier.District, 20));
                    Check.True("X7 精细度拉满 ⇒ 容差比默认档紧一个数量级（回到旧版那种严丝合缝）",
                        PolicyKit.ChordTolerance(cfg, AreaTier.District, 20, 1.0) * 8 < PolicyKit.ChordTolerance(cfg, AreaTier.District, 20));
                    Check.Close("X7 类别级默认取该档**最松**的一界（不等宽度信息就凭空收紧会压死滑杆）",
                        1.2, PolicyKit.ChordToleranceBand(AreaTier.Lot, 0), 1e-12);
                    Check.True("X7 Resolve 里给的是带宽上界，不是 3 米兜底", () =>
                    {
                        var t = PolicyKit.Resolve(cfg, AreaTier.Lot, 12);
                        return Math.Abs(t.ChordTol - PolicyKit.ChordToleranceBand(AreaTier.Lot, t.CurveDetail)) < 1e-12;
                    });

                    Check.True("X7 硬界只封顶不抬底：滑杆要更松时被夹住，要更密时不受影响", () =>
                    {
                        var fine = FixToBudget(0.01, 0.4);
                        var coarse = FixToBudget(50.0, 0.4);
                        return fine > coarse && coarse <= 24;
                    });

                    var arc = Curve(200);
                    Check.Close("X7 四分之一圆弧的整段弦高 = R(1-1/√2) = 58.58",
                        200 * (1 - 1 / Math.Sqrt(2)), GeoKit.ChordDeviation(arc, 0, arc.Length2D()), 0.05);
                    Check.Close("X7 三点外接圆：R=50", 50, GeoKit.CircumRadius(Fix.XY(50, 0), Fix.XY(0, 50), Fix.XY(-50, 0)), 1e-9);
                    Check.True("X7 三点共线 ⇒ 半径 +∞（直路不该因此铺点）",
                        double.IsPositiveInfinity(GeoKit.CircumRadius(Fix.XY(0, 0), Fix.XY(10, 0), Fix.XY(20, 0))));
                    Check.True("X7 直路上 ChordSafePoints 一个中间点都不加", () =>
                    {
                        var straight = Fix.Straight(Fix.XY(0, 0), Fix.XY(500, 0), 20);
                        var pts = GeoKit.ChordSafePoints(straight, 0, straight.Length2D(), 0.5, 2, 100);
                        return pts.Count == 0;
                    });
                    Check.True("X7 弯道上的 ChordSafePoints 守住弦高：逐根弦量都不超过容差", () =>
                    {
                        double tol = 0.5;
                        double len = arc.Length2D();
                        var pts = GeoKit.ChordSafePoints(arc, 0, len, tol, 2, 200);
                        if (pts.Count < 4) return false;
                        double prev = 0;
                        for (int i = 0; i < pts.Count; i++)
                        {
                            P3 hit;
                            int si;
                            double st, at, dist;
                            GeoKit.ClosestPointOnPolyline(arc, pts[i], out hit, out si, out st, out at, out dist);
                            if (GeoKit.ChordDeviation(arc, prev, at) > tol * 1.001) return false;
                            prev = at;
                        }
                        return GeoKit.ChordDeviation(arc, prev, len) <= tol * 1.001;
                    });
                    Check.True("X7 半径越大 ⇒ 同容差下**单位弧长**的点越稀（弯越急点越密）", () =>
                    {
                        // 弦长 ∝ √R（L=√(8Rh)），所以密度 ∝ 1/√R：R 从 60 到 600 是 10 倍，
                        // 密度该差 √10≈3.16 倍。比点数是错的 —— 两条弧一样长，点数只会跟着弧长走。
                        var t = Curve(60);
                        var g = Curve(600);
                        double lt = t.Length2D(), lg = g.Length2D();
                        int nt = GeoKit.ChordSafePoints(t, 0, lt, 0.3, 0.5, 400).Count;
                        int ng = GeoKit.ChordSafePoints(g, 0, lg, 0.3, 0.5, 400).Count;
                        double dt = nt / lt, dg = ng / lg;
                        return nt > 0 && ng > 0 && dt > dg * 2.5;
                    });
                });

                // —————————————————————————————— 编码与锚点工具
                Harness.Section("AnchorCode 编码互不冲突（新增建筑档之后）（X8）", () =>
                {
                    Check.True("X8 四类负数区间两两不重叠", () =>
                    {
                        int[] probes = { -1, -999999, -1000000, -1999999, -2000000, -2999999, -3000000, -3999999 };
                        int hits = 0;
                        for (int i = 0; i < probes.Length; i++)
                        {
                            int n = 0;
                            if (AnchorCode.IsBorder(probes[i])) n++;
                            if (AnchorCode.IsTile(probes[i])) n++;
                            if (AnchorCode.IsShore(probes[i])) n++;
                            if (AnchorCode.IsObject(probes[i])) n++;
                            if (n != 1) return false;
                            hits++;
                        }
                        return hits == probes.Length;
                    });
                    Check.True("X8 网络边号不被当成负数档（老代码用 <= -2e6 判海岸，会把建筑档误判进去）", () =>
                        AnchorCode.IsNet(0) && AnchorCode.IsNet(7)
                        && !AnchorCode.IsShore(AnchorCode.Object(3)) && AnchorCode.IsObject(AnchorCode.Object(3)));
                    Check.Int("X8 编解码互逆（边界）", 12, AnchorCode.Decode(AnchorCode.Border(12), AnchorCode.BORDER_BASE));
                    Check.Int("X8 编解码互逆（建筑）", 4, AnchorCode.Decode(AnchorCode.Object(4), AnchorCode.OBJECT_BASE));
                    Check.True("X8 GraphEdge.LineFor：缺侧线时退回中心线而不是 null", () =>
                    {
                        int ei;
                        var w = Road(false, out ei);
                        var ge = w.Edges[0];
                        ge.RightLine = null;
                        return ReferenceEquals(ge.LineFor(PlacedNode.SIDE_RIGHT), ge.Line)
                            && ReferenceEquals(ge.LineFor(PlacedNode.SIDE_LEFT), ge.LeftLine);
                    });
                    Check.True("X8 SignedSide 认得中心线两侧（跨越判据的地基）", () =>
                    {
                        int ei;
                        var w = Road(false, out ei);
                        return SnapKit.SignedSide(w.Edges[0].Line, Fix.XY(50, 3)) > 0
                            && SnapKit.SignedSide(w.Edges[0].Line, Fix.XY(50, -3)) < 0
                            && SnapKit.SignedSide(w.Edges[0].Line, Fix.XY(50, 0)) == 0;
                    });
                });

                Harness.Section("GraphEdge 每侧独立半幅宽（W，反馈 3：人行道外缘）", () =>
                {
                    // 这一算式上一版写在 GameSide/WorldSampler 里 ⇒ 只有开游戏才看得见对错。
                    // 现在它挪到 Engine（GraphEdge.SetWidthsFromPrefab），因为玩家反馈 3 的
                    // 「边缘指人行道外缘，不是车道路缘」整条就系在这两个数上：
                    // 不对称断面（单边人行道 / 中央分隔带 / 隔音墙）时两侧不再相等。
                    var ge = new GraphEdge();

                    ge.SetWidthsFromPrefab(20, 0);
                    Check.Close("W 对称断面 ⇒ 两侧各等于半幅（左）", 10, ge.LeftHalfWidth, 1e-12);
                    Check.Close("W 对称断面 ⇒ 两侧各等于半幅（右）", 10, ge.RightHalfWidth, 1e-12);

                    ge.SetWidthsFromPrefab(20, 4);
                    Check.Close("W 中线偏移 +4 ⇒ 外侧那一边变成 14（人行道全在那侧）", 14, ge.LeftHalfWidth, 1e-12);
                    Check.Close("W 内侧那边同时变成 6（两侧之和仍等于整幅宽，宽度没被重复计）", 6, ge.RightHalfWidth, 1e-12);
                    Check.Close("W 两侧之和 = m_Width", 20, ge.LeftHalfWidth + ge.RightHalfWidth, 1e-12);
                    Check.Close("W 原始偏移留着（诊断日志要能看出这条路不对称）", 4, ge.MiddleOffset, 1e-12);

                    ge.SetWidthsFromPrefab(20, -4);
                    Check.Close("W 偏移取负 ⇒ 两侧数字互换（左右跟着游戏同号约定翻边）", 6, ge.LeftHalfWidth, 1e-12);
                    Check.Close("W 偏移取负 ⇒ 另一边 14", 14, ge.RightHalfWidth, 1e-12);

                    ge.SetWidthsFromPrefab(20, 12);
                    Check.True("W 偏移超过半幅时两侧都还是非负数（缘线翻到中心另一侧，距离不能是负的）", () =>
                        ge.LeftHalfWidth >= 0 && ge.RightHalfWidth >= 0);
                    Check.Close("W 偏移超过半幅：内侧 = |10-12| = 2", 2, ge.RightHalfWidth, 1e-12);
                    Check.Close("W 偏移超过半幅：外侧 = |10+12| = 22", 22, ge.LeftHalfWidth, 1e-12);

                    ge.SetWidthsFromPrefab(double.NaN, double.NaN);
                    Check.True("W 脏输入（NaN）⇒ 消毒成 0，绝不把 NaN 传进走线", () =>
                        !double.IsNaN(ge.LeftHalfWidth) && !double.IsNaN(ge.RightHalfWidth)
                        && !double.IsNaN(ge.RoadWidth) && ge.LeftHalfWidth == 0 && ge.RightHalfWidth == 0);
                    ge.SetWidthsFromPrefab(-30, 5);
                    Check.True("W 负宽度 ⇒ 当 0（读不到），但两侧仍各自按算式取非负", () =>
                        ge.RoadWidth == 0 && ge.LeftHalfWidth == 5 && ge.RightHalfWidth == 5);
                    ge.SetWidthsFromPrefab(12, 0);
                    Check.Close("W 半幅 0 之后还能被正常值覆盖回来（采样器一条边只调一次，但顺序不保证）",
                        6, ge.LeftHalfWidth, 1e-12);

                    // 消费侧：TraceKit 的剪角与 LineFor 都必须按「这一侧自己的」半幅走，
                    // 两侧都按 RoadWidth/2 剪就是老写法，玩家看到的症状是「内侧那条线卡在车道边上」。
                    var w = new WorldSnapshot();
                    w.Nodes.Add(Fix.Node(0, 0, 0));
                    w.Nodes.Add(Fix.Node(1, 100, 0));
                    var e = Fix.Edge(0, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(0, 0), Fix.XY(100, 0), 10));
                    e.SetWidthsFromPrefab(20, 4);                      // 左 14 / 右 6
                    e.LeftLine = Fix.Straight(Fix.XY(0, 14), Fix.XY(100, 14), 10);
                    e.RightLine = Fix.Straight(Fix.XY(0, -6), Fix.XY(100, -6), 10);
                    w.Edges.Add(e);
                    w.BuildAdjacency();

                    Check.True("W LineFor 按侧取各自的折线（不是同一条按半幅平移）", () =>
                        ReferenceEquals(e.LineFor(PlacedNode.SIDE_LEFT), e.LeftLine)
                        && ReferenceEquals(e.LineFor(PlacedNode.SIDE_RIGHT), e.RightLine));

                    var lotT = PolicyKit.Resolve(cfg, AreaTier.Lot, 20);
                    // 点 (50, 14) 离左缘 0 米、离中心线 14 米；点 (50, -6) 离右缘 0 米。
                    var onLeft = SnapKit.FindSnap(Fix.XY(50, 14), w, cfg, lotT, Fix.Free(Fix.XY(50, 14)));
                    var onRight = SnapKit.FindSnap(Fix.XY(50, -6), w, cfg, lotT, Fix.Free(Fix.XY(50, -6)));
                    Check.Enum("W 外侧那一边吸到的就是 y=14 那条线（人行道外缘）", SnapKind.NetSide, onLeft.Kind);
                    Check.True("W 外侧落点 y=14", () => Harness.Near(onLeft.Pos, Fix.XY(50, 14), 1e-9));
                    Check.Bool("W 外侧定侧为 LEFT", true, onLeft.Side == PlacedNode.SIDE_LEFT);
                    Check.True("W 内侧落点 y=-6（老写法会把它算成 y=-10：整条内缘偏 m_MiddleOffset 米）",
                        () => Harness.Near(onRight.Pos, Fix.XY(50, -6), 1e-9));
                    Check.Bool("W 内侧定侧为 RIGHT", true, onRight.Side == PlacedNode.SIDE_RIGHT);

                    // 描边也必须沿各自那条线：两端都吸在内侧时，走线贴着 y=-6，而不是 y=-10。
                    var tr = TraceKit.Trace(onRight, SnapKit.FindSnap(Fix.XY(80, -6), w, cfg, lotT, Fix.Free(Fix.XY(80, -6))),
                        w, cfg, lotT, Fix.EmptyCtx());
                    Check.True("W 沿内侧描边：每个中间点都躺在 y=-6 那条线上", () =>
                    {
                        if (!tr.UsedNetwork) return false;
                        for (int i = 0; i < tr.Points.Count; i++)
                            if (Math.Abs(tr.Points[i].Y + 6) > 1e-6) return false;
                        return true;
                    });
                    Check.True("W 沿外侧描边同理：两端吸外侧时走线在 y=14", () =>
                    {
                        var b2 = SnapKit.FindSnap(Fix.XY(80, 14), w, cfg, lotT, Fix.Free(Fix.XY(80, 14)));
                        var r2 = TraceKit.Trace(onLeft, b2, w, cfg, lotT, Fix.EmptyCtx());
                        if (!r2.UsedNetwork) return false;
                        for (int i = 0; i < r2.Points.Count; i++)
                            if (Math.Abs(r2.Points[i].Y - 14) > 1e-6) return false;
                        return true;
                    });
                });
            }

            // ———— 小工具 ————

            /// <summary>结果里是否存在某个「离 target 不到 1 毫米」的点。</summary>
            private static bool HasNear(List<P3> pts, P3 target)
            {
                if (pts == null) return false;
                for (int i = 0; i < pts.Count; i++) if (pts[i].DistanceTo(target) < 1e-3) return true;
                return false;
            }

            /// <summary>R=200 米的四分之一圆弧，2 米步长密采（与游戏侧同口径）。</summary>
            private static Polyline Curve(double r)
            {
                int n = (int)Math.Round(Math.PI * 0.5 * r / 2.0);
                var line = new Polyline();
                for (int i = 0; i <= n; i++)
                {
                    double a = (Math.PI * 0.5) * i / n;
                    line.Add(new P3(r * Math.Cos(a), r * Math.Sin(a), 0));
                }
                return line;
            }

            /// <summary>在 R=200 的弯上跑一遍 FitToBudget，返回交出去的点数（用来验硬界只封顶不抬底）。</summary>
            private static int FixToBudget(double tolerance, double hard)
            {
                var pts = Curve(200).Points;
                double used;
                var r = SimplifyKit.FitToBudget(pts, tolerance, hard, 2.0, 4000, null, out used);
                return r.Count;
            }
        }
    }
}
