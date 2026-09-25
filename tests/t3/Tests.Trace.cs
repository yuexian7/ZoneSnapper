using System;
using System.Collections.Generic;
using ZoneSnapper.Engine;

namespace ZoneSnapper.T3
{
    internal static partial class Tests
    {
        /// <summary>
        /// TraceKit：把「两点直连」换成「沿网络走过去」。原模组 Subdivisions 的更新日志里
        /// 0.2.3 修的两个 bug（边界自交、区域塌成薄片）全都出在这一层，所以这里两边都测：
        /// 该描边的要描到、该回退的必须回退、回退原因还得说对（实机调参靠它）。
        /// </summary>
        internal static class Trace
        {
            public static void Run()
            {
                var cfg = ModConfig.CreateDefault();

                Harness.Section("TraceKit 同一曲线切子段（T）", () =>
                {
                    var w = Fix.OneStraightEdge(100, 10);
                    var tuning = Fix.TraceTuning(AreaTier.District);
                    var a = Fix.OnEdge(Fix.XY(20, 0), 0, 20);
                    var b = Fix.OnEdge(Fix.XY(80, 0), 0, 80);
                    var r = TraceKit.Trace(a, b, w, cfg, tuning, Fix.EmptyCtx());
                    Check.Bool("T 用上了网络", true, r.UsedNetwork);
                    Check.Enum("T 无回退", TraceFallback.None, r.Fallback);
                    // 第六轮反馈 1 的新契约：Points 只含交点/切换 pin 与曲率补点，不再保住首末关节点。
                    // 直线子段 ⇒ 一个自动点都没有。下面「中间点的位置/顺序」这类路由检验，
                    // 直线夹具已经验不动了（点列为空），换到带两个真角的 Zed 夹具上做。
                    Check.Int("T 直线子段 ⇒ 0 个自动点（新契约：首末关节点不再交付）", 0, r.Points.Count);
                    Check.True("T 无任何点离端点近于 MinSpacing（游戏三角化吃的就是重合点）", () =>
                    {
                        for (int i = 0; i < r.Points.Count; i++)
                        {
                            if (r.Points[i].DistanceTo(a.Pos) < tuning.MinSpacing - 1e-9) return false;
                            if (r.Points[i].DistanceTo(b.Pos) < tuning.MinSpacing - 1e-9) return false;
                        }
                        return true;
                    });
                    Check.Close("T 代价 = 子段弧长 60", 60, r.Cost, 1e-9);
                    Check.Int("T 只用了一条边", 1, r.UsedEdges.Count);
                    Check.Int("T 用的边号", 0, r.UsedEdges.Count > 0 ? r.UsedEdges[0] : -1);
                    Check.True("T 走廊包围盒含两端点", () => r.Corridor.IsValid
                        && r.Corridor.Contains(new P3(20, 0, 0)) && r.Corridor.Contains(new P3(80, 0, 0)));
                    Check.True("T 边签名带回来了（需求 5 的增量判定靠它）", () => r.Signature == w.Edges[0].Signature);

                    var rev = TraceKit.Trace(b, a, w, cfg, tuning, Fix.EmptyCtx());
                    Check.Int("T 反向：直线子段同样 0 个自动点", 0, rev.Points.Count);

                    // 路由与方向检验换带真角的夹具：一条 Z 形单折线路（0,0)→(40,0)→(40,30)→(80,30），
                    // 两个 90° 拐角都是交点 ⇒ 新契约下正好各留一个 pin，直段部分照旧不补点。
                    var zw = Zed();
                    var bend1 = Fix.XY(40, 0);
                    var bend2 = Fix.XY(40, 30);
                    var za = Fix.OnEdge(Fix.XY(10, 0), 0, 10);
                    var zb = Fix.OnEdge(Fix.XY(70, 30), 0, 100);
                    var zr = TraceKit.Trace(za, zb, zw, cfg, tuning, Fix.EmptyCtx());
                    Check.Enum("T Z 形子段无回退", TraceFallback.None, zr.Fallback);
                    Check.Close("T Z 形子段代价 = 走廊弧长 30+30+30 = 90", 90, zr.Cost, 1e-9);
                    Check.True("T 中间点全落在两手动节点之间（不含两端）：只剩两个拐角 pin，且都离开端点至少 MinSpacing", () =>
                    {
                        if (zr.Points.Count != 2) return false;
                        if (!HasNear(zr.Points, bend1) || !HasNear(zr.Points, bend2)) return false;
                        for (int i = 0; i < zr.Points.Count; i++)
                        {
                            if (zr.Points[i].DistanceTo(za.Pos) < tuning.MinSpacing - 1e-9) return false;
                            if (zr.Points[i].DistanceTo(zb.Pos) < tuning.MinSpacing - 1e-9) return false;
                        }
                        return true;
                    });
                    var zrev = TraceKit.Trace(zb, za, zw, cfg, tuning, Fix.EmptyCtx());
                    Check.True("T 反向：走线顺序跟着反（x 递减），投影才不会打结", () =>
                    {
                        if (zrev.Points.Count != 2) return false;
                        var full = new List<P3> { zb.Pos };
                        full.AddRange(zrev.Points);
                        full.Add(za.Pos);
                        for (int i = 1; i < full.Count; i++) if (full[i].X > full[i - 1].X + 1e-9) return false;
                        return true;
                    });
                    Check.True("T 反向：首点靠近本条边的出发端、末点靠近到达端", () =>
                        zrev.Points[0].DistanceTo(zb.Pos) < zrev.Points[zrev.Points.Count - 1].DistanceTo(zb.Pos)
                        && HasNear(zrev.Points, bend1) && HasNear(zrev.Points, bend2));
                });

                Harness.Section("TraceKit 跨边沿图走（不斜穿）（T2）", () =>
                {
                    var g = Fix.SquareRing(100, 10, 0);
                    var tuning = Fix.TraceTuning(AreaTier.District);
                    // a 在底边靠近 n1、b 在顶边靠近 n2 ⇒ 唯一合理走线是 a→n1→(右边)→n2→b
                    var a = Fix.OnEdge(Fix.XY(80, 0), g.E0, 80);
                    var b = Fix.OnEdge(Fix.XY(80, 100), g.E2, 20);
                    var r = TraceKit.Trace(a, b, g.World, cfg, tuning, Fix.EmptyCtx());
                    Check.Bool("T2 跨边描边用上了网络", true, r.UsedNetwork);
                    Check.Enum("T2 无回退", TraceFallback.None, r.Fallback);
                    Check.Close("T2 代价 = 20(进 n1)+100(右边 E1)+20(出 n2) = 140", 140, r.Cost, 1e-9);
                    Check.True("T2 每个返回点都贴在某个网络折线上（不许斜穿街区）", () => AllNearNetwork(r.Points, g.World, 1.0));
                    Check.True("T2 拐角保住了：走的就是右边（该边两个接头 n1/n2 都在结果里，斜穿街区时一个都不会有）", () =>
                        HasNear(r.Points, Fix.XY(100, 0)) && HasNear(r.Points, Fix.XY(100, 100)));
                    Check.True("T2 用到的边包含中间那条（E1）", () => r.UsedEdges.Contains(g.E1));
                    // 新口径（第六轮反馈 1）：直线段的首末关节点不再交付，简化后只剩「必须留」的点 ——
                    // 这条走线在 n1(100,0)、n2(100,100) 各拐一个 90° 角 ⇒ 恰好 2 个交点 pin。
                    // 下限从老口径的 3 降到 2；上限 14 照旧（简化闸整个失效时这里必须红）。
                    Check.True("T2 简化后点数落在 2..14（10 米采样 11 点必须被压掉，两个拐角 pin 要在）", () =>
                        r.Points.Count >= 2 && r.Points.Count <= 14);

                    // 跨 kind：第五轮反馈 5 把「允许跨网络类型描边」那个开关删了，换成了**贴合模式**。
                    // 于是这一对断言正好钉住那次替换的语义等价：NoNetworkSwitch 模式 = 原来的开关关掉。
                    var lshape = Fix.LShape(100, 10, out int ln0, out int ln1, out int ln2, out int le0, out int le1);
                    var la = Fix.OnEdge(Fix.XY(80, 0), le0, 80);
                    var lb = Fix.OnEdge(Fix.XY(100, 80), le1, 80);
                    var tNoSwitch = Fix.TunMode(AreaTier.District, 60, 0.5, 60, 2.0, 3.0, TraceMode.NoNetworkSwitch);
                    var rOff = TraceKit.Trace(la, lb, lshape, cfg, tNoSwitch, Fix.EmptyCtx());
                    Check.Enum("T2 模式=不切换网络 ⇒ 跨 kind 回退 NotOnNetwork", TraceFallback.NotOnNetwork, rOff.Fallback);
                    Check.Int("T2 模式=不切换网络 ⇒ 一个点都不给", 0, rOff.Points.Count);
                    var rOn = TraceKit.Trace(la, lb, lshape, cfg, tuning, Fix.EmptyCtx());
                    Check.Bool("T2 默认（智能）模式 ⇒ 道路+铁路照样描边", true, rOn.UsedNetwork);
                    Check.True("T2 默认模式 ⇒ 结果沿边不斜穿", () => AllNearNetwork(rOn.Points, lshape, 1.0));
                    Check.True("T2 切换处放了节点：跨 kind 的走线里含那个接头 (100,0)", () => HasNear(rOn.Points, Fix.XY(100, 0)));
                });

                Harness.Section("TraceKit 多跳走廊的代价对账（T2b）", () =>
                {
                    // 方形环上 a 在底边靠西、b 在右边靠北。两条候选走线：
                    //   沿底边向东 80 m 到 n1，再沿右边向北 80 m 到 b        ⇒ 160
                    //   沿底边向西 20 m 到 n0，绕左/上两条边 200 m，再向南 20 m ⇒ 240
                    // Cost 是 MaxDetourFactor 判定的输入，报大了就会把能描的边判成超预算；
                    // 老实现只能从「较近的接头」进出图，所以只能找到 240 那条（回归壳逼出来的多源/多汇重写）。
                    var g = Fix.SquareRing(100, 10, 0);
                    var tuning = Fix.TraceTuning(AreaTier.District);
                    var a = Fix.OnEdge(Fix.XY(20, 0), g.E0, 20);          // 底边靠西：近的接头是 n0
                    var b = Fix.OnEdge(Fix.XY(100, 80), g.E1, 80);        // 右边靠北：近的接头是 n2
                    var r = TraceKit.Trace(a, b, g.World, cfg, tuning, Fix.EmptyCtx());
                    Check.True("T2b 两跳走廊应当描得出来（绕行比 160/113.1 = 1.41 < 默认 3）", () => r.UsedNetwork && r.Fallback == TraceFallback.None);
                    Check.True("T2b 结果沿边不斜穿", () => AllNearNetwork(r.Points, g.World, 1.0));
                    Check.Close("T2b Cost = 全网最短路 160（进 80 + 出 80），不是绕大圈的 240",
                        160, r.Cost, 1e-9);
                    Check.True("T2b 走的是东边近路（绕大圈的话必然出现顶边 y≈100 的点）", () =>
                    {
                        if (r.Points.Count == 0) return false;
                        for (int i = 0; i < r.Points.Count; i++) if (r.Points[i].Y > 90) return false;
                        return true;
                    });
                    // 预算刚好够的时候也必须描出来：160 ≤ 113.1×2
                    var t2 = Fix.Tun(AreaTier.District, 60, 0.5, 60, 2.0, 2.0);
                    var r2 = TraceKit.Trace(a, b, g.World, cfg, t2, Fix.EmptyCtx());
                    Check.True("T2b 预算 2.0（226 ≥ 160）⇒ 仍该描出来，不许因虚高代价退回直线", () =>
                        r2.UsedNetwork && r2.Fallback == TraceFallback.None);
                    // 预算压到 1.3（226→147 < 160）时才该拒：这时拒得对，且原因必须写清是「绕行太远」
                    var t3 = Fix.Tun(AreaTier.District, 60, 0.5, 60, 2.0, 1.3);
                    var r3 = TraceKit.Trace(a, b, g.World, cfg, t3, Fix.EmptyCtx());
                    Check.Enum("T2b 预算 1.3（147 < 160）⇒ 回退原因 DetourTooLong", TraceFallback.DetourTooLong, r3.Fallback);
                    Check.Int("T2b 超预算 ⇒ 一个点都不给（交回游戏直连）", 0, r3.Points.Count);
                });

                Harness.Section("TraceKit 预算与不可达 ⇒ 回退直线（T3）", () =>
                {
                    // 两点之间没有路：图不连通
                    var w = new WorldSnapshot();
                    w.Nodes.Add(Fix.Node(0, 0, 0)); w.Nodes.Add(Fix.Node(1, 100, 0));
                    w.Nodes.Add(Fix.Node(2, 0, 200)); w.Nodes.Add(Fix.Node(3, 100, 200));
                    w.Edges.Add(Fix.Edge(0, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(0, 0), Fix.XY(100, 0), 10)));
                    w.Edges.Add(Fix.Edge(1, NetKind.Rail, 2, 3, Fix.Straight(Fix.XY(0, 200), Fix.XY(100, 200), 10)));
                    w.BuildAdjacency();
                    var tuning = Fix.TraceTuning(AreaTier.District);
                    var a = Fix.OnEdge(Fix.XY(10, 0), 0, 10);
                    var b = Fix.OnEdge(Fix.XY(10, 200), 1, 10);
                    var r = TraceKit.Trace(a, b, w, cfg, tuning, Fix.EmptyCtx());
                    Check.True("T3 不可达 ⇒ 一定有回退原因（不是 None）", () => r.Fallback != TraceFallback.None);
                    Check.Int("T3 不可达 ⇒ 零个点（交给游戏直连）", 0, r.Points.Count);
                    Check.Bool("T3 不可达 ⇒ 没假装用过网络", false, r.UsedNetwork);

                    // 一条 5 段折尺路：东 20 m ⇒ 北 20 m ⇒ 东 10 m。
                    // 走线弧长 = 5(进) + 10 + 10 + 10 + 5(出) = 40；两端直线 = √(20²+20²) = 28.2843
                    // ⇒ 绕行比 1.41421。**这条走廊必须带弯**：原来那版五段链全是直线，绕行比恒等于 1，
                    // 「超预算」那组断言其实什么都没验到（引擎当时给的 48 才是真值，注释里的 51 是笔误）。
                    var chain = new WorldSnapshot();
                    for (int i = 0; i <= 5; i++)
                    {
                        double[] nx = { 0, 10, 20, 20, 20, 30 };
                        double[] nz = { 0, 0, 0, 10, 20, 20 };
                        chain.Nodes.Add(Fix.Node(i, nx[i], nz[i]));
                    }
                    for (int i = 0; i < 5; i++)
                        chain.Edges.Add(Fix.Edge(i, NetKind.Road, i, i + 1,
                            Fix.Straight(Fix.XY(chain.Nodes[i].Pos.X, chain.Nodes[i].Pos.Y),
                                         Fix.XY(chain.Nodes[i + 1].Pos.X, chain.Nodes[i + 1].Pos.Y), 10)));
                    chain.BuildAdjacency();
                    var ca = Fix.OnEdge(Fix.XY(5, 0), 0, 5);
                    var cb = Fix.OnEdge(Fix.XY(25, 20), 4, 5);
                    var corridor = Fix.Poly(0, 0, 20, 0, 20, 20, 30, 20);

                    // 预算 1.30：28.2843×1.30 = 36.77 < 40 ⇒ 超预算，必须整体作废
                    var tight = Fix.Tun(AreaTier.District, 60, 0.5, 60, 2.0, 1.30);
                    var rt = TraceKit.Trace(ca, cb, chain, cfg, tight, Fix.EmptyCtx());
                    Check.Int("T3 绕行比超预算（1.414 > 1.30）⇒ 零个点", 0, rt.Points.Count);
                    Check.True("T3 绕行比超预算 ⇒ 有回退原因", () => rt.Fallback != TraceFallback.None);
                    Check.Enum("T3 绕行比超预算 ⇒ 原因写「绕行太远」（DetourTooLong）：日志与提示靠它，实机不能让玩家猜",
                        TraceFallback.DetourTooLong, rt.Fallback);

                    // 预算 1.50：28.2843×1.50 = 42.43 ≥ 40 ⇒ 该描出来，且代价是真实弧长而不是「弧长+启发项」
                    var okT = Fix.Tun(AreaTier.District, 60, 0.5, 60, 2.0, 1.50);
                    var rok = TraceKit.Trace(ca, cb, chain, cfg, okT, Fix.EmptyCtx());
                    Check.True("T3 绕行比 1.414 < 预算 1.50 ⇒ 五段折尺路必须描出来（真被游戏画成直线=描边功能失效）",
                        () => rok.UsedNetwork && rok.Fallback == TraceFallback.None);
                    Check.Close("T3 Cost 必须等于走廊真实弧长 40（A* 启发项不许混进 g）", 40, rok.Cost, 1e-9);
                    Check.True("T3 描出来的点全在那条折尺路上", () =>
                    {
                        if (rok.Points.Count == 0) return false;
                        for (int i = 0; i < rok.Points.Count; i++) if (Harness.DistToLine(corridor, rok.Points[i]) > 1e-9) return false;
                        return true;
                    });
                    Check.True("T3 两个拐角都保住了（(20,0) 与 (20,20) 少任何一个就成了两段折线近似）", () =>
                        HasNear(rok.Points, Fix.XY(20, 0)) && HasNear(rok.Points, Fix.XY(20, 20)));
                    Check.Int("T3 用到的边数 = 5（整条折尺路都算进了签名，需求 5 的增量重算靠它）", 5, rok.UsedEdges.Count);
                });

                Harness.Section("TraceKit 环形绕行（需求 8 / 原模组 0.2.3 薄片）（T4）", () =>
                {
                    var g = Fix.SquareRing(100, 10, 0);
                    Check.True("T4 四节点环：HasAlternatePathBetween(n0,n1,E0) 为真", () =>
                        g.World.HasAlternatePathBetween(g.N0, g.N1, g.E0));
                    Check.True("T4 环上每条边都标了 OnRing", () =>
                    {
                        foreach (var e in g.World.Edges) if (!e.OnRing) return false;
                        return true;
                    });
                    Check.False("T4 同一点没有「另一条路」", () => g.World.HasAlternatePathBetween(g.N0, g.N0, g.E0));
                    var lshape = Fix.LShape(100, 10, out _, out _, out _, out int le0, out _);
                    Check.False("T4 L 形（非环）没有另一条路", () => lshape.HasAlternatePathBetween(0, 1, le0));

                    // 「薄片」形状：底边是一条深弓（弧长 608，两端直线距离 100）。
                    // 直切子段会把区域压成一条缝 ⇒ 必须绕环的另一侧走。
                    var bow = Fix.SquareRing(100, 10, 0);
                    bow.World.Edges[g.E0].Line = Fix.Poly(0, 0, 25, -150, 50, -300, 75, -150, 100, 0);
                    bow.World.Edges[g.E0].Length = bow.World.Edges[g.E0].Line.Length2D();
                    bow.World.InvalidateIndex();
                    bow.World.BuildAdjacency();
                    double total = bow.World.Edges[g.E0].Length;
                    Check.InRange("T4 构造有效：弓形底边弧长 600..620", total, 600, 620);

                    var a = Fix.OnEdge(Fix.XY(0, 0), g.E0, 0);
                    var b = Fix.OnEdge(Fix.XY(100, 0), g.E0, total);
                    var tuning = Fix.Tun(AreaTier.District, 60, 0.5, 60, 2.0, 6.0);
                    var r = TraceKit.Trace(a, b, bow.World, cfg, tuning, Fix.EmptyCtx());
                    Check.Enum("T4 绕行成功 ⇒ 无回退", TraceFallback.None, r.Fallback);
                    Check.Bool("T4 绕行用上了网络", true, r.UsedNetwork);
                    Check.True("T4 绕行没有退化成弓形薄片（没有一个点沉到 y<-1）", () =>
                    {
                        for (int i = 0; i < r.Points.Count; i++) if (r.Points[i].Y < -1) return false;
                        return true;
                    });
                    var corridor = Fix.Poly(0, 0, 0, 100, 100, 100, 100, 0);
                    // 新口径（第六轮反馈 1）：直线段的首末关节点不再交付 ⇒ 这条绕行只剩 n3、n2
                    // 两个 90° 拐角 pin（n0/n1 与手动端点重合，被哨兵吸收）。所以「沿另一侧走」
                    // 改按 [a]+Points+[b] 的完整边验：每一格都贴走廊，且两个拐角必须都在。
                    Check.True("T4 绕行结果沿环的另一侧走（[a]+Points+[b] 每格离「n0→n3→n2→n1」走廊 ≤1 米，两拐角 pin 都在）", () =>
                    {
                        if (!HasNear(r.Points, Fix.XY(0, 100)) || !HasNear(r.Points, Fix.XY(100, 100))) return false;
                        var full = new List<P3> { a.Pos };
                        full.AddRange(r.Points);
                        full.Add(b.Pos);
                        for (int i = 0; i < full.Count; i++) if (Harness.DistToLine(corridor, full[i]) > 1.0) return false;
                        return true;
                    });
                    Check.InRange("T4 绕行代价 ≈ 300（三边）而直切是 608", r.Cost, 299, 330);
                });

                Harness.Section("TraceKit 绕行开关关掉也必须回退薄片（T5）", () =>
                {
                    // 单条非环边，子段弧长远超 MaxDetourFactor×直线距离：设计文档 §四写的是无条件安全阀
                    // 「代价超过 k×直线距离 或搜索超预算 → 回退直线」。这条与 TraceAroundRings 无关。
                    var w = new WorldSnapshot();
                    w.Nodes.Add(Fix.Node(0, 0, 0));
                    w.Nodes.Add(Fix.Node(1, 100, 0));
                    var line = Fix.Poly(0, 0, 25, -150, 50, -300, 75, -150, 100, 0);
                    w.Edges.Add(Fix.Edge(0, NetKind.Road, 0, 1, line));
                    w.BuildAdjacency();
                    double total = w.Edges[0].Length;
                    var a = Fix.OnEdge(Fix.XY(0, 0), 0, 0);
                    var b = Fix.OnEdge(Fix.XY(100, 0), 0, total);
                    var tuning = Fix.Tun(AreaTier.District, 60, 0.5, 60, 2.0, 3.0);
                    var r = TraceKit.Trace(a, b, w, cfg, tuning, Fix.EmptyCtx());
                    Check.True("T5 超绕行预算 ⇒ 必须给回退原因（当前直接吐了薄片结果）", () => r.Fallback != TraceFallback.None);
                    Check.Int("T5 超绕行预算 ⇒ 零个点，让游戏按直线画", 0, r.Points.Count);
                });

                Harness.Section("TraceKit 贴到交叉口不得倒着拽路（T6）", () =>
                {
                    // SnapKit 给 NetNode 候选的 Arc 恒为 0（SnapKit.cs:56）。当这个交叉口在它所属边的
                    // 「终点」那一头时，AppendAnchorSegment 会把整条边从另一头拽进来 ⇒ 边界先倒着走一遍再折回。
                    var w = new WorldSnapshot();
                    w.Nodes.Add(Fix.Node(0, 0, 0));       // 交叉口（玩家贴上的那个点）
                    w.Nodes.Add(Fix.Node(1, 100, 0));     // 东边
                    w.Nodes.Add(Fix.Node(2, 0, 100));     // 北边
                    w.Edges.Add(Fix.Edge(0, NetKind.Road, 1, 0, Fix.Straight(Fix.XY(100, 0), Fix.XY(0, 0), 10)));  // 注意：n0 是这条边的「终点」
                    w.Edges.Add(Fix.Edge(1, NetKind.Road, 0, 2, Fix.Straight(Fix.XY(0, 0), Fix.XY(0, 100), 10)));
                    w.BuildAdjacency();
                    var a = Fix.AtNode(Fix.XY(0, 0), 0, 0, 0);        // 与 SnapKit 产出完全一致：Arc=0
                    var b = Fix.OnEdge(Fix.XY(0, 60), 1, 60);
                    var tuning = Fix.TraceTuning(AreaTier.District);
                    var r = TraceKit.Trace(a, b, w, cfg, tuning, Fix.EmptyCtx());
                    // 新口径（第六轮反馈 1）：a→b 就是沿北边那条路的直段 ⇒ Points 为空是正确行为。
                    // 「不许倒退」改按 [a]+Points+[b] 的完整边验，并且要求真的用上了网络、代价等于
                    // 沿路 60 米（否则「空点列」可能只是没描边的空，检验就成了恒真）。
                    Check.True("T6 交叉口出发的描边不得出现「沿另一条路倒退」的点（[a]+Points+[b] 全贴在本该走的北边线上）", () =>
                    {
                        if (!r.UsedNetwork || r.Fallback != TraceFallback.None) return false;
                        var north = Fix.Poly(0, 0, 0, 100);
                        var full = new List<P3> { a.Pos };
                        full.AddRange(r.Points);
                        full.Add(b.Pos);
                        for (int i = 0; i < full.Count; i++) if (Harness.DistToLine(north, full[i]) > 1.0) return false;
                        return true;
                    });
                    Check.Close("T6 代价 = 沿北边的 60 米（真沿路走过去，不是退回直线也没倒着拽路）", 60, r.Cost, 1e-9);
                    Check.True("T6 顺带：结果里没有任何点落在东边那条路上（y==0 且 x>1）", () =>
                    {
                        for (int i = 0; i < r.Points.Count; i++)
                        {
                            if (Math.Abs(r.Points[i].Y) < 1e-9 && r.Points[i].X > 1.0) return false;
                        }
                        return true;
                    });
                    // 最直白的一条：交付给游戏的那条折线本身不得比「直线 × 绕行倍率」更长（设计文档 §四的安全阀）。
                    // 内部 Cost 记的是图搜索代价，掩盖了折线实际长度，所以这里按折线长度重算。
                    Check.True("T6 交付折线的实际长度 ≤ MaxDetourFactor×直线距离（不得回踩折返）", () =>
                    {
                        if (r.Points.Count == 0) return true;
                        var full = new List<P3> { a.Pos };
                        full.AddRange(r.Points);
                        full.Add(b.Pos);
                        double len = new Polyline(full).Length2D();
                        double straight = a.Pos.DistanceTo(b.Pos);
                        return len <= straight * tuning.MaxDetourFactor + 1e-9;
                    });
                });

                Harness.Section("TraceKit 开关/异常输入与缓存复用（T7）", () =>
                {
                    var g = Fix.SquareRing(100, 10, 0);
                    var tuning = Fix.TraceTuning(AreaTier.District);
                    var a = Fix.OnEdge(Fix.XY(50, 0), g.E0, 50);
                    var b = Fix.OnEdge(Fix.XY(80, 0), g.E0, 80);

                    var noTrace = Fix.Tun(AreaTier.District, 60, 0.5, 60, 2.0, 3.0);
                    noTrace.TraceEnabled = false;
                    var rd = TraceKit.Trace(a, b, g.World, cfg, noTrace, Fix.EmptyCtx());
                    Check.Enum("T7 描边开关关掉 ⇒ Disabled 且零点", TraceFallback.Disabled, rd.Fallback);
                    Check.Int("T7 Disabled ⇒ 零个点", 0, rd.Points.Count);
                    Check.Enum("T7 world==null 也不抛，按 Disabled", TraceFallback.Disabled,
                        TraceKit.Trace(a, b, null, cfg, tuning, Fix.EmptyCtx()).Fallback);

                    // ——————— 第八轮反馈 2/3/6/10 定的口径（这一段是那一轮改行为的正面记录）
                    //
                    // 玩家那一格**一动不动**；走线自己在他旁边长出一个接入点，然后走正常描边那条路（A*）。
                    // 「节点续接」从此退成兜底：只有那一格附近真没有本档该贴的东西时才轮到它。
                    // 老断言写的正好相反（「自由端 ⇒ 走续接」「两端都自由 ⇒ 整条手动」），
                    // 那两条合起来就是反馈 2 的两句投诉：关掉续接后什么都不贴、开着续接就强行断在路口。
                    var far = TraceKit.Trace(Fix.Free(Fix.XY(0, 0)), b, g.World, cfg, tuning, Fix.EmptyCtx());
                    Check.True("T7 一端是自由点 ⇒ 接入点模型：走线接进网络并描边成功（反馈 2/6）", () =>
                        far.UsedNetwork && far.Fallback == TraceFallback.None);
                    Check.False("T7 能连通时不许走续接那一条支路（反馈 2「变成强制续接」的根因就在这）", () => far.Continued);
                    var contOff = Fix.Tun(AreaTier.District, 60, 0.5, 60, 2.0, 3.0);
                    contOff.ContinuationEnabled = false;
                    var farOff = TraceKit.Trace(Fix.Free(Fix.XY(0, 0)), b, g.World, cfg, contOff, Fix.EmptyCtx());
                    Check.True("T7 续接关掉 ⇒ 自动贴合照样生效（反馈 2 第一句：关掉后预览直接不生效）", () =>
                        farOff.UsedNetwork && farOff.Fallback == TraceFallback.None && !farOff.Continued);
                    var both = TraceKit.Trace(Fix.Free(Fix.XY(0, 0)), Fix.Free(Fix.XY(50, 50)), g.World, cfg, tuning, Fix.EmptyCtx());
                    Check.True("T7 两端都自由、但都够得着网络 ⇒ 照样接进去贴（反馈 6/8/10）", () =>
                        both.UsedNetwork && both.Attached >= 1 && both.Fallback == TraceFallback.None);
                    Check.True("T7 长出来的那个接入点必须真的在网络上（反馈 10：只有自动路径上的点按规则吸附）", () =>
                        both.Points.Count >= 1 && Harness.Near(both.Points[0], Fix.XY(50, 0), 1e-9));
                    // 真的四周什么都没有 ⇒ 这才叫手动路径（保持玩家自己画的那条直线）。
                    var nowhere = TraceKit.Trace(Fix.Free(Fix.XY(0, -500)), Fix.Free(Fix.XY(50, -500)),
                        g.World, cfg, tuning, Fix.EmptyCtx());
                    Check.Enum("T7 两端附近都没有可贴的东西 ⇒ NotOnNetwork（手动路径）", TraceFallback.NotOnNetwork, nowhere.Fallback);
                    Check.Int("T7 手动路径不许有自动点", 0, nowhere.Points.Count);
                    var same = TraceKit.Trace(a, Fix.OnEdge(Fix.XY(50.5, 0), g.E0, 50.5), g.World, cfg, tuning, Fix.EmptyCtx());
                    Check.Enum("T7 两点近于最小间距 ⇒ SameAnchor", TraceFallback.SameAnchor, same.Fallback);

                    // 陈旧边下标：世界快照是按走廊现抓的，删了路之后手动栈里可能留着越界的 Edge。
                    // 这条在游戏里发生在 ToolUpdate 相位 —— 抛异常就是整个区域工具卡死。
                    Check.Guard("T7 越界边下标不得抛异常（模组自己写出的陈旧栈必须能兜住）", () =>
                    {
                        var stale = Fix.OnEdge(Fix.XY(50, 0), 9999, 50);
                        var rs = TraceKit.Trace(stale, b, g.World, cfg, tuning, Fix.EmptyCtx());
                        Check.True("T7 越界边下标 ⇒ 回退且零点", () => rs.Fallback != TraceFallback.None && rs.Points.Count == 0);
                    });

                    // —— 区域边界的子段描边（负数 Edge 编码）
                    var bw = new WorldSnapshot();
                    bw.Borders.Add(Fix.Border(7, AreaTier.District, Fix.Poly(0, 0, 50, 30, 100, 0)));
                    double blen = bw.Borders[0].Line.Length2D();
                    var ba = Fix.OnBorder(Fix.XY(0, 0), 0, 0);
                    var bb = Fix.OnBorder(P3.Lerp(Fix.XY(50, 30), Fix.XY(100, 0), 0.5), 0, blen * 0.75);
                    var br = TraceKit.Trace(ba, bb, bw, cfg, tuning, Fix.EmptyCtx());
                    Check.Bool("T7 同一条区域边界：沿边界描边成功", true, br.UsedBorder);
                    Check.True("T7 边界描边点数合理且不含两端", () =>
                    {
                        for (int i = 0; i < br.Points.Count; i++)
                        {
                            if (br.Points[i].DistanceTo(ba.Pos) < tuning.MinSpacing) return false;
                            if (br.Points[i].DistanceTo(bb.Pos) < tuning.MinSpacing) return false;
                        }
                        return br.Points.Count >= 1;
                    });
                    var cross = TraceKit.Trace(ba, Fix.OnBorder(Fix.XY(50, 30), 1, 0), bw, cfg, tuning, Fix.EmptyCtx());
                    Check.Enum("T7 跨两条不同边界 ⇒ 不描边（NotOnNetwork）", TraceFallback.NotOnNetwork, cross.Fallback);

                    // v0.1.4：「海岸线贴合」整条删除。这里换成**删除后的守护**：
                    // 一个带旧海岸档（枚举值 1 已空出来）+ 旧 -(2e6+s) 编码的节点，
                    // 必须安静地走直线回退 —— 不抛、不描出一条已经不存在的线。
                    var legacyA = new PlacedNode(Fix.XY(0, 0), (SnapKind)1, -1, AnchorCode.Shore(0), 0, 0);
                    var legacyB = new PlacedNode(Fix.XY(100, 0), (SnapKind)1, -1, AnchorCode.Shore(0), 80, 0);
                    var legacy = TraceKit.Trace(legacyA, legacyB, new WorldSnapshot(), cfg, tuning, Fix.EmptyCtx());
                    Check.Enum("T7 旧海岸档 ⇒ NotOnNetwork 直线回退（删除后不抛、不描）", TraceFallback.NotOnNetwork, legacy.Fallback);
                    Check.Empty("T7 旧海岸档不产出描边点", legacy.Points);

                    // —— 增量复用（需求 5 的命脉）
                    var st = new ManualStack();
                    st.Push(Fix.Free(Fix.XY(0, 0)));
                    st.Push(Fix.Free(Fix.XY(100, 0)));
                    st.SetEdge(0, Fix.TraceOf(Fix.XY(50, 1)));
                    Check.False("T7 未盖章的边不算新鲜", () => st.EdgeIsFresh(0, 555));
                    st.StampEdge(0, 555);
                    Check.True("T7 盖章后同签名 ⇒ 复用", () => st.EdgeIsFresh(0, 555));
                    Check.False("T7 世界签名变了 ⇒ 必须重算", () => st.EdgeIsFresh(0, 556));
                    st.MarkAllStale();
                    Check.False("T7 MarkAllStale 之后 EdgeIsFresh 必须为假（否则「网络脏了」根本不会触发重算）",
                        () => st.EdgeIsFresh(0, 555));
                    Check.Int("T7 Edges 数量恒为 Nodes-1", st.Nodes.Count - 1, st.Edges.Count);
                    Check.True("T7 Clear 之后彻底干净", () =>
                    {
                        st.Clear();
                        return st.Count == 0 && st.Edges.Count == 0 && st.Signature == 0;
                    });
                });

                Harness.Section("TraceKit 节点续接的契约（T8，第五轮反馈 7）", () =>
                {
                    // 玩家原话：「没有连续路径的时候，先贴出一段或者多段，然后在两个中断的节点之间
                    // 用直线连接；必须至少有 1 个节点处在道路/轨道/建筑的边缘上；
                    // 绕行距离与续接角度依然受规则束缚；续接的节点可以是手动节点，也可以是续接路径上的节点。」
                    // 这几句逐条对应下面的断言。
                    //
                    // 夹具：一条东西向的路 n0(0,0)—e0—n1(100,0)，再加一条从 n0 往南的 n0—e1—n2(0,-50)。
                    // 锚点 a 在 e0 上靠东、自由端 b 在南边 ⇒ 续接只能往西走到 n0，
                    // 而 n0 之后唯一「离自由端更近」的下一跳就是那条南向的路，转角正好 90°（> 75° 上限）：
                    // 于是「停在哪一格」正好只由角度闸决定。
                    var w = new WorldSnapshot();
                    w.Nodes.Add(Fix.Node(0, 0, 0));
                    w.Nodes.Add(Fix.Node(1, 100, 0));
                    w.Nodes.Add(Fix.Node(2, 0, -50));
                    var e0 = Fix.Edge(0, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(0, 0), Fix.XY(100, 0), 10));
                    var e1 = Fix.Edge(1, NetKind.Road, 0, 2, Fix.Straight(Fix.XY(0, 0), Fix.XY(0, -50), 10));
                    e0.RoadWidth = 20;
                    e1.RoadWidth = 20;
                    w.Edges.Add(e0);
                    w.Edges.Add(e1);
                    w.BuildAdjacency();
                    var cfg = ModConfig.CreateDefault();
                    var t = Fix.Tun(AreaTier.District, 60, 0.5, 60, 2.0, 3.0);
                    var a = Fix.OnEdge(Fix.XY(95, 0), 0, 95);
                    var freeEnd = Fix.Free(Fix.XY(-10, -120));
                    var stop = Fix.XY(0, 0);             // 续接该停下来的那个接头
                    var hairpin = Fix.XY(0, -50);        // 那条 90° 掉头路上的点

                    // ①「必须至少 1 个节点在边缘上」：只有一端在路上 ⇒ 那一端起照样贴
                    var r1 = TraceKit.Trace(a, freeEnd, w, cfg, t, Fix.EmptyCtx());
                    Check.True("T8 ① 一端在路上 ⇒ 续接成立（UsedNetwork + Continued）", () =>
                        r1.UsedNetwork && r1.Continued && r1.Fallback == TraceFallback.None);
                    // 新口径（第六轮反馈 1）：直段不再交付首末关节点 ⇒ 那 95 米直路上只剩「中断点」
                    // 一个 pin。「照样贴出来了」按 [a]+Points 的折线长度验：它必须正好是那 95 米，
                    // 少一格（续接根本没走）或短一截（半路断了）这里都会红。
                    Check.True("T8 ① 续接不是一格都不给：[a]+Points 正好铺满沿路那 95 米（新契约下直段只剩中断 pin）", () =>
                    {
                        if (r1.Points.Count < 1) return false;
                        var full = new List<P3> { a.Pos };
                        full.AddRange(r1.Points);
                        return Harness.Near(new Polyline(full).Length2D(), 95.0, 1e-6);
                    });
                    Check.True("T8 「中断处」那一格就是走线的最后一格（反馈 7：续接节点可以是续接路径上的节点）", () =>
                        Harness.Near(r1.Points[r1.Points.Count - 1], stop, 1e-6));
                    Check.True("T8 中断那一格进了 Pins（简化闸不许把它抹掉，否则直线接不回任何节点上）", () =>
                        HasNear(r1.Pins, stop));

                    // ②「续接角度受规则束缚」：90° 的掉头路口不许跟过去
                    Check.False("T8 ② 转角 90° > 75° 上限 ⇒ 不许跳到那条掉头路（宁可在角上断）", () =>
                        HasNear(r1.Points, hairpin));
                    Check.True("T8 ② 走线上没有任何一格越过中断点的南边", () =>
                    {
                        for (int i = 0; i < r1.Points.Count; i++) if (r1.Points[i].Y < -1e-6) return false;
                        return true;
                    });

                    // ③「两端都不在路上」⇒ 根本不续接，这一条边就是玩家自己画的手动路径。
                    //    ⚠ 第八轮反馈 2/6/10 之后，「自由」得拆成两种：
                    //     · 附近**有**本档该贴的东西（在吸附半径内）⇒ 走线长出接入点，正常描边（见 T7 与下面的 ③b）；
                    //     · 附近**没有**（半径外）⇒ 才是这里说的手动路径。
                    //    老断言用 (50,30) 那种「离路 30 米」的点，在半径 60 米的夹具里现在会接进去 ⇒ 挪到半径外。
                    var r3 = TraceKit.Trace(Fix.Free(Fix.XY(50, 300)), freeEnd, w, cfg, t, Fix.EmptyCtx());
                    Check.Enum("T8 ③ 两端附近都没有可贴的东西 ⇒ NotOnNetwork，零个点（手动路径）", TraceFallback.NotOnNetwork, r3.Fallback);
                    Check.Int("T8 ③ 手动路径不许有自动点", 0, r3.Points.Count);

                    // ③b 一半够得着、一半够不着 ⇒ 够得着那端接进去，够不着那端才留给续接（反馈 2/6 的合读）。
                    var r3b = TraceKit.Trace(Fix.Free(Fix.XY(50, 30)), freeEnd, w, cfg, t, Fix.EmptyCtx());
                    Check.True("T8 ③b 一端在半径内 ⇒ 先长接入点，再从那端续接到另一端", () =>
                        r3b.UsedNetwork && r3b.Attached >= 1 && r3b.Fallback == TraceFallback.None);
                    Check.True("T8 ③b 接进去那一格确实落在路上（不是凭空的折角）", () =>
                        HasNear(r3b.Points, Fix.XY(50, 0)));

                    // ④「贴出来的长度太短就不要」：断在半路又立刻接直线 = 边界上无缘无故的小折角。
                    //    这一档只管**续接**那条支路，所以得把吸附关掉来测 ——
                    //    第八轮之后吸附开着时这一格会走接入点那条路（长出来的点是真节点，不是半截折角），
                    //    下面那条 ④b 就是这个区别。
                    var noSnap = Fix.Tun(AreaTier.District, 60, 0.5, 60, 2.0, 3.0);
                    noSnap.SnapEnabled = false;
                    // 一条**只有自身**的直路（没有第二条边可跳），从西端头起：续接一步都走不出去。
                    var solo = Fix.OneStraightEdge(100, 10);
                    var r4 = TraceKit.Trace(Fix.OnEdge(Fix.XY(0, 0), 0, 0), Fix.Free(Fix.XY(-10, -60)), solo, cfg, noSnap, Fix.EmptyCtx());
                    Check.Enum("T8 ④ 一步都贴不出来（< 6 米下限）⇒ 宁可不续接", TraceFallback.NotOnNetwork, r4.Fallback);
                    Check.Int("T8 ④ 不续接时零点（这一条边走玩家自己画的直线）", 0, r4.Points.Count);

                    var r4b = TraceKit.Trace(Fix.OnEdge(Fix.XY(3, 0), 0, 3), Fix.Free(Fix.XY(3, 15)), w, cfg, t, Fix.EmptyCtx());
                    Check.True("T8 ④b 吸附开着 ⇒ 同一条边改走接入点：那一段是真贴出来的，不是半截折角", () =>
                        r4b.UsedNetwork && r4b.Attached >= 1 && r4b.Fallback == TraceFallback.None);

                    // ⑤ 预算闸：绕行上限压到 1.05 倍时，那条 215 米的续接根本不该成立
                    var tightTuning = Fix.Tun(AreaTier.District, 60, 0.5, 60, 2.0, 1.05);
                    var r5 = TraceKit.Trace(a, freeEnd, w, cfg, tightTuning, Fix.EmptyCtx());
                    Check.Enum("T8 ⑤ 绕行预算不够 ⇒ 直接不续接（Cost 里连末段直线一起算）", TraceFallback.NotOnNetwork, r5.Fallback);
                    Check.Int("T8 ⑤ 超预算时零点", 0, r5.Points.Count);
                    Check.True("T8 ⑤ 预算够（3 倍）时确实贴出来了 ⇒ 上面那条是预算拦的，不是别的闸", () =>
                        r1.Cost > a.Pos.DistanceTo(freeEnd.Pos) * 1.05 && r1.Cost <= a.Pos.DistanceTo(freeEnd.Pos) * 3.0 + 1e-6);

                    // ⑥ 走线顺序必须恒为 a→b（预览与落档都按这个顺序编号）
                    var r6 = TraceKit.Trace(freeEnd, a, w, cfg, t, Fix.EmptyCtx());
                    Check.True("T8 ⑥ 交换两端后点数相同", () => r6.Points.Count == r1.Points.Count);
                    Check.True("T8 ⑥ 交换两端后走线正好反序（第一格仍是那个中断点）", () =>
                    {
                        if (r6.Points.Count != r1.Points.Count || r6.Points.Count == 0) return false;
                        if (!Harness.Near(r6.Points[0], stop, 1e-6)) return false;
                        for (int i = 0; i < r6.Points.Count; i++)
                            if (!Harness.Near(r6.Points[i], r1.Points[r1.Points.Count - 1 - i], 1e-9)) return false;
                        return true;
                    });

                    // ⑦ 存档侧的账：续接也要写清用了哪几条边（跟随系统靠它做增量大算）
                    Check.True("T8 ⑦ 续接记录了用到的边，且签名非 0（缓存复用的判据）", () =>
                        r1.UsedEdges.Count >= 1 && r1.Signature != 0);
                    Check.True("T8 ⑦ 两次同样的续接结果逐格相同（幂等：跟随每帧重算才敢用）", () =>
                    {
                        var again = TraceKit.Trace(a, freeEnd, w, cfg, t, Fix.EmptyCtx());
                        if (again.Points.Count != r1.Points.Count) return false;
                        for (int i = 0; i < again.Points.Count; i++)
                            if (!Harness.Near(again.Points[i], r1.Points[i], 1e-12)) return false;
                        return again.Signature == r1.Signature;
                    });

                    // ⑧ 两端都在路上时轮不到续接（那是正常描边，不是续接）
                    var bothNet = TraceKit.Trace(a, Fix.OnEdge(Fix.XY(20, 0), 0, 20), w, cfg, t, Fix.EmptyCtx());
                    Check.False("T8 ⑧ 正常描边不许被标成续接（日志与预览靠这个区分）", () => bothNet.Continued);
                });

                Harness.Section("TraceKit 接入点模型：玩家那格不动、走线自己接进网络（T12，第八轮反馈 2/6/8/10）", () =>
                {
                    var cfg = ModConfig.CreateDefault();

                    // 【反馈 6 的那个例子】两条互相垂直的贯通路（东西向 100 米 + 从它东端往北 100 米），
                    // 玩家两头各点在路旁 20 米的地方 —— 他要的就是"首尾相切 45° 的那条四分之一大弯路"。
                    // 绕行比 1.414：老口径被「距离一远就不许贴」的绝对余量搅过，现在纯比例闸下必须贴得下来。
                    int ln0, ln1, ln2, le0, le1;
                    var lw = Fix.LShape(100, 10, out ln0, out ln1, out ln2, out le0, out le1);
                    var lt = Fix.Tun(AreaTier.District, 60, 0.5, 60, 2.0, 3.0);
                    var fa = Fix.Free(Fix.XY(0, -20));
                    var fb = Fix.Free(Fix.XY(120, 100));
                    var bend = TraceKit.Trace(fa, fb, lw, cfg, lt, Fix.EmptyCtx());
                    Check.True("T12 两端都离路 20 米 ⇒ 两个接入点，走线照样通（反馈 6/8/10）", () =>
                        bend.UsedNetwork && bend.Attached == 2 && bend.Fallback == TraceFallback.None);
                    Check.False("T12 这不是续接（能连通的时候不许走贪心那一条，反馈 2「强制续接」）", () => bend.Continued);
                    Check.True("T12 走线 = (0,0) → (100,0) → (100,100)：拐点落在路的中轴交点上", () =>
                        bend.Points.Count == 3
                        && Harness.Near(bend.Points[0], Fix.XY(0, 0), 1e-9)
                        && Harness.Near(bend.Points[1], Fix.XY(100, 0), 1e-9)
                        && Harness.Near(bend.Points[2], Fix.XY(100, 100), 1e-9));
                    Check.True("T12 玩家那两个端点没有被写进走线（模组长的是自动点，不是把他的点挪过来）", () =>
                    {
                        for (int i = 0; i < bend.Points.Count; i++)
                        {
                            if (bend.Points[i].DistanceTo(fa.Pos) < 1.0) return false;
                            if (bend.Points[i].DistanceTo(fb.Pos) < 1.0) return false;
                        }
                        return true;
                    });
                    Check.Close("T12 绕行比就是 1.414（四分之一弯路本来该是这个数）",
                        1.4142135624, bend.Cost / fa.Pos.DistanceTo(fb.Pos), 1e-6);

                    // 【反馈 2 的第一句】节点续接关掉之后，上面这一整套必须**照原样**成立 ——
                    // 老写法里续接是唯一能产出网络点的通路，关掉它等于把自动贴合整个关掉。
                    var ltOff = Fix.Tun(AreaTier.District, 60, 0.5, 60, 2.0, 3.0);
                    ltOff.ContinuationEnabled = false;
                    var bendOff = TraceKit.Trace(fa, fb, lw, cfg, ltOff, Fix.EmptyCtx());
                    Check.True("T12 关掉续接 ⇒ 同一条走线逐格全等（预览/落档都不许变）", () =>
                    {
                        if (bendOff.Points.Count != bend.Points.Count) return false;
                        if (bendOff.Attached != bend.Attached) return false;
                        if (bendOff.Fallback != TraceFallback.None) return false;
                        for (int i = 0; i < bend.Points.Count; i++)
                            if (!Harness.Near(bendOff.Points[i], bend.Points[i], 1e-12)) return false;
                        return true;
                    });

                    // 【反馈 6 的正面说法】「节点间无论距离多少都要尝试自动贴合」：
                    // 两点相距 141 米（绕一圈 200 米），比例闸按尺度无关的方式放行；
                    // 而同样这两点，中间没有贯通的路时才会退回直线。
                    var g = Fix.SquareRing(100, 10, 0);
                    var t = Fix.TraceTuning(AreaTier.District);
                    var wide = TraceKit.Trace(Fix.Free(Fix.XY(0, 0)), Fix.Free(Fix.XY(100, 100)), g.World, cfg, t, Fix.EmptyCtx());
                    Check.True("T12 相距一个街区对角线 ⇒ 仍然沿网络绕（距离本身不是门槛）", () =>
                        wide.UsedNetwork && wide.Fallback == TraceFallback.None && wide.Cost > 100.0);
                    var offGrid = Fix.OneStraightEdge(100, 10);
                    var lonely = TraceKit.Trace(Fix.Free(Fix.XY(50, 400)), Fix.Free(Fix.XY(60, 500)), offGrid, cfg, t, Fix.EmptyCtx());
                    Check.Enum("T12 附近确实没有路 ⇒ NotOnNetwork（该直连的时候老实直连）", TraceFallback.NotOnNetwork, lonely.Fallback);
                    Check.Int("T12 这种情形一个自动点都不许多", 0, lonely.Points.Count);
                });

                Harness.Section("TraceKit 交点与切换处必放节点（T9，第五轮反馈 4）", () =>
                {
                    var cfg = ModConfig.CreateDefault();

                    // —— 交点的定义：折线处。转角 ≥ 拐点判定 ⇒ 必须留一个节点，任何简化闸都不许抹掉它。
                    // 构造一条「缓折」：两臂各 10 米、转角 30°，整段弦高偏差只有 2.59 米 ——
                    // 默认档（市辖区、路宽 20 米）的弦高容差是 5 米，所以**不钉它就会被 RDP 抹掉**，
                    // 于是「这一点在不在」正好只由交点规则决定，不是采样碰巧。
                    // 两臂各按 1 米采样（游戏侧 WorldSampler.SAMPLE_STEP=2 米，这里取更密的 1 米：
                    // MarkCorners 要看的是相邻两段的方向差，太稀就退化成「只有折点一个格」，判不了角）。
                    var kink = new Polyline();
                    var arm1 = Fix.Straight(Fix.XY(0, 0), Fix.XY(10, 0), 10);
                    var arm2 = Fix.Straight(Fix.XY(10, 0), Fix.XY(10 + 10 * Math.Cos(30 * Math.PI / 180.0),
                        10 * Math.Sin(30 * Math.PI / 180.0)), 10);
                    for (int i = 0; i < arm1.Count; i++) kink.Add(arm1.Points[i]);
                    for (int i = 1; i < arm2.Count; i++) kink.Add(arm2.Points[i]);
                    var kw = new WorldSnapshot();
                    kw.Nodes.Add(Fix.Node(0, kink.Points[0].X, kink.Points[0].Y));
                    kw.Nodes.Add(Fix.Node(1, kink.Points[kink.Count - 1].X, kink.Points[kink.Count - 1].Y));
                    var ke = Fix.Edge(0, NetKind.Road, 0, 1, kink);
                    ke.RoadWidth = 20;
                    ke.Length = kink.Length2D();
                    kw.Edges.Add(ke);
                    kw.BuildAdjacency();
                    var ka = Fix.OnEdge(kink.Points[0], 0, 0);
                    var kb = Fix.OnEdge(kink.Points[kink.Count - 1], 0, kink.Length2D());
                    var coarse = Fix.Tun(AreaTier.District, 60, 50.0, 60, 2.0, 3.0);      // 松到该档硬界 5 米
                    Check.True("T9 前置：这条缓折线的中间采样格够多（MarkCorners 要 ≥3 格才判得了方向差）", () => kink.Count >= 12);
                    Check.True("T9 前置：那个缓折的整段弦高偏差确实小于容差（否则下面那条断言是假的）："
                        + Check.Fmt(GeoKit.ChordDeviation(kink, 0, kink.Length2D())), () =>
                        GeoKit.ChordDeviation(kink, 0, kink.Length2D())
                        < PolicyKit.ChordTolerance(cfg, AreaTier.District, 20, 0.0));
                    var withCorner = TraceKit.Trace(ka, kb, kw, cfg, coarse, Fix.EmptyCtx());
                    var noCorner = coarse;
                    noCorner.CornerDegrees = 179;                                          // 判定关掉：处处都不算交点
                    var withoutCorner = TraceKit.Trace(ka, kb, kw, cfg, noCorner, Fix.EmptyCtx());
                    Check.True("T9 钉住那一格确实多花了一格（新契约下不钉就是 0 格：缓弯弦高在容差内，直线段不补点）", () =>
                        withCorner.Points.Count == withoutCorner.Points.Count + 1);
                    Check.True("T9 交点必放节点：30° 的缓折在默认精细度（0）下仍然留了一格（转角判定 22°）", () =>
                        HasNear(withCorner.Points, kink.Points[10]));
                    Check.True("T9 同一夹具把交点判定关掉 ⇒ 那一格被简化抹掉（证明上面靠的是交点规则，不是采样）", () =>
                        !HasNear(withoutCorner.Points, kink.Points[10]));
                    Check.True("T9 交点那一格进了 Pins（钉住它才谈得上「任何简化闸都不许删」）", () =>
                        HasNear(withCorner.Pins, kink.Points[10]));
                    Check.Empty("T9 判定关掉时一个钉都不登记（钉不是「每个采样点都留」）", withoutCorner.Pins);

                    // —— 曲线精细度越高，交点判定越严：同一个 6° 的缓折，默认档不算交点、100% 档算。
                    //    （上面那条 30° 的角两档都算，所以它证明不了「精细度」这一维，只能证明「交点」这一维。）
                    var gentle = new Polyline();
                    var g1 = Fix.Straight(Fix.XY(0, 0), Fix.XY(20, 0), 20);
                    var g2 = Fix.Straight(Fix.XY(20, 0), Fix.XY(20 + 20 * Math.Cos(6 * Math.PI / 180.0),
                        20 * Math.Sin(6 * Math.PI / 180.0)), 20);
                    for (int i = 0; i < g1.Count; i++) gentle.Add(g1.Points[i]);
                    for (int i = 1; i < g2.Count; i++) gentle.Add(g2.Points[i]);
                    var gw = new WorldSnapshot();
                    gw.Nodes.Add(Fix.Node(0, gentle.Points[0].X, gentle.Points[0].Y));
                    gw.Nodes.Add(Fix.Node(1, gentle.Points[gentle.Count - 1].X, gentle.Points[gentle.Count - 1].Y));
                    var ge2 = Fix.Edge(0, NetKind.Road, 0, 1, gentle);
                    ge2.RoadWidth = 20;
                    ge2.Length = gentle.Length2D();
                    gw.Edges.Add(ge2);
                    gw.BuildAdjacency();
                    var ga = Fix.OnEdge(gentle.Points[0], 0, 0);
                    var gb = Fix.OnEdge(gentle.Points[gentle.Count - 1], 0, gentle.Length2D());
                    P3 gentleKink = gentle.Points[20];
                    Check.True("T9 前置：这条缓折的转角 6°（比默认判定小、比 100% 档判定大）", () =>
                    {
                        double deg = PolicyKit.Resolve(ModConfig.CreateDefault(), AreaTier.District, 20).CornerDegrees;
                        return 6.0 < deg && 6.0 > PolicyKit.Resolve(Detail(1.0), AreaTier.District, 20).CornerDegrees;
                    });
                    Check.True("T9 默认档（精细度 0，判定 22°）⇒ 6° 的缓折不算交点、不钉（反馈 4「没有交点就不放」）", () =>
                    {
                        var r = TraceKit.Trace(ga, gb, gw, cfg, Fix.Tun(AreaTier.District, 60, 50.0, 60, 2.0, 3.0), Fix.EmptyCtx());
                        return !HasNear(r.Pins, gentleKink);
                    });
                    Check.True("T9 精细度拉到 100% ⇒ 阈值收到基准的 20%（4.4°），同一个 6° 缓折就算交点了", () =>
                    {
                        var fine = Fix.Tun(AreaTier.District, 60, 50.0, 60, 2.0, 3.0);
                        fine.CornerDegrees = PolicyKit.Resolve(Detail(1.0), AreaTier.District, 20).CornerDegrees;
                        if (!(fine.CornerDegrees > 4.0 && fine.CornerDegrees < 4.8)) return false;
                        var r = TraceKit.Trace(ga, gb, gw, Detail(1.0), fine, Fix.EmptyCtx());
                        return HasNear(r.Pins, gentleKink);
                    });
                    Check.True("T9 同一个 30° 角在两档精细度下都算交点（精细度只放宽「多小的角才算」，不豁免明显的角）", () =>
                    {
                        var fine = Fix.Tun(AreaTier.District, 60, 50.0, 60, 2.0, 3.0);
                        fine.CornerDegrees = PolicyKit.Resolve(Detail(1.0), AreaTier.District, 20).CornerDegrees;
                        var r = TraceKit.Trace(ka, kb, kw, Detail(1.0), fine, Fix.EmptyCtx());
                        return HasNear(r.Points, kink.Points[10]) && HasNear(r.Pins, kink.Points[10]);
                    });

                    // —— 没有交点就不放：一条笔直的路，两端之间除了必要的间距点以外不该有折点
                    var straightW = Fix.OneStraightEdge(200, 20);
                    var sa = Fix.OnEdge(Fix.XY(0, 0), 0, 0);
                    var sb = Fix.OnEdge(Fix.XY(200, 0), 0, 200);
                    var sr = TraceKit.Trace(sa, sb, straightW, cfg, Fix.Tun(AreaTier.District, 60, 5.0, 60, 2.0, 3.0), Fix.EmptyCtx());
                    Check.Empty("T9 完全笔直 ⇒ 一个交点都没有，不该钉任何点（反馈 4：没有交点就不放）", sr.Pins);
                    // 第六轮反馈 1 的本体：直线边上不许在手动节点旁边多留自动点。
                    // 老实现的简化闸无条件保首末格，而点列不含手动端点 ⇒ 直线也留 1~2 个黄点；
                    // 现在 Finish 把两个手动端点当哨兵送进简化再剥掉（TraceKit.Finish 注释）。
                    Check.Int("T9 完全笔直的边 ⇒ 0 个自动点（第六轮反馈 1）", 0, sr.Points.Count);
                    Check.True("T9 直路上的自动点（若有）全部躺在同一条直线上", () =>
                    {
                        for (int i = 0; i < sr.Points.Count; i++)
                            if (Math.Abs(sr.Points[i].Y) > 1e-9) return false;
                        return true;
                    });

                    // —— 切换处：道路→铁路那个接头必须留节点，且与「交点」重合时不许变成两格
                    //    （反馈 4：切换处与交点可以是同一坐标，游戏会自动合并同坐标节点）
                    var lw = Fix.LShape(100, 10, out int ln0, out int ln1, out int ln2, out int le0, out int le1);
                    var la = Fix.OnEdge(Fix.XY(80, 0), le0, 80);
                    var lb = Fix.OnEdge(Fix.XY(100, 80), le1, 80);
                    var lr = TraceKit.Trace(la, lb, lw, cfg, Fix.Tun(AreaTier.District, 60, 0.5, 60, 2.0, 3.0), Fix.EmptyCtx());
                    Check.Int("T9 切换处那一格在结果里只出现一次（同坐标不重复堆放）", 1, CountNear(lr.Points, Fix.XY(100, 0)));
                    Check.True("T9 道路→铁路的接头进了 Pins", () => HasNear(lr.Pins, Fix.XY(100, 0)));

                    // —— 高度切换：地面道路 ↔ 高架道路，即使平面方向完全没拐，也要在切换处留一格
                    var hw = new WorldSnapshot();
                    hw.Nodes.Add(Fix.Node(0, 0, 0));
                    hw.Nodes.Add(Fix.Node(1, 100, 0));
                    hw.Nodes.Add(Fix.Node(2, 200, 0));
                    var he0 = Fix.Edge(0, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(0, 0), Fix.XY(100, 0), 10));
                    var he1 = Fix.Edge(1, NetKind.Road, 1, 2, Fix.Straight(Fix.XY(100, 0), Fix.XY(200, 0), 10));
                    he0.RoadWidth = 20; he1.RoadWidth = 20;
                    he1.Elevated = true; he1.Lift = 8;      // 同一条东西向的路，后半段上了高架
                    hw.Edges.Add(he0); hw.Edges.Add(he1);
                    hw.BuildAdjacency();
                    var ha = Fix.OnEdge(Fix.XY(20, 0), 0, 20);
                    var hb = Fix.OnEdge(Fix.XY(180, 0), 1, 80);
                    var hr = TraceKit.Trace(ha, hb, hw, cfg, Fix.Tun(AreaTier.District, 60, 0.5, 60, 2.0, 3.0), Fix.EmptyCtx());
                    Check.True("T9 地面↔高架的切换处放了节点（反馈 4：可以切到不同高度，切换处必须放节点）", () =>
                        hr.UsedNetwork && HasNear(hr.Pins, Fix.XY(100, 0)));
                    Check.Int("T9 高度切换处同样只留一格", 1, CountNear(hr.Points, Fix.XY(100, 0)));
                    var hrSame = TraceKit.Trace(ha, hb, hw, cfg, Fix.Tun(AreaTier.District, 60, 0.5, 60, 2.0, 3.0), Fix.EmptyCtx());
                    Check.True("T9 两条都是地面路时不因「高度」多钉（本夹具里第二档改回地面）", () =>
                    {
                        he1.Elevated = false; he1.Lift = 0;
                        var r = TraceKit.Trace(ha, hb, hw, cfg, Fix.Tun(AreaTier.District, 60, 0.5, 60, 2.0, 3.0), Fix.EmptyCtx());
                        return !HasNear(r.Pins, Fix.XY(100, 0)) && hrSame.UsedNetwork;
                    });
                });

                Harness.Section("TraceKit 弯道节点账（曲线精细度三档要能被机器复核）（C9）", () =>
                {
                    // 一条 R=200 m 的四分之一圆弧路，按游戏侧采样步长（WorldSampler.SAMPLE_STEP=2 m）
                    // 密采成 158 点。原模组 Subdivisions 就是在这种地方一路铺点；本模组承诺的是
                    // 「同样贴着路，点数少一个数量级」，那这句话就必须有断言盯着，不能只写在 README 里。
                    //
                    // ⚠ 第五轮反馈 7 把这一档改名成**曲线精细度**并**反转了方向**：
                    //   旧 Simplification 0 = 几乎不简化（点最多）；新 CurveDetail 0 = 只在交点放点（点最少）。
                    //   默认值随之从上一版的中间值变成 0 —— 玩家要的就是「别在缓弯上一路铺点」。
                    var dense = Arc(200, 2);
                    Check.Int("C9 采样基线：弧长 314 m ⇒ 158 个采样点", 158, dense.Count);

                    var fine = Run(dense, AreaTier.Lot, 12, 1.0);       // 精细度 100%：回到严丝合缝
                    var mid = Run(dense, AreaTier.Lot, 12, 0.5);
                    var coarse = Run(dense, AreaTier.Lot, 12, 0.0);     // 默认档：只在交点放点
                    Check.True("C9 精细度 100% ⇒ 节点压到 30 个以内（仍比 158 的密采样少一个数量级）：" + fine.nodes, () =>
                        fine.nodes <= 30 && fine.nodes > 4);
                    Check.True("C9 精细度 100% 的精度：整条弯到真圆弧的最大偏差不超过 0.5 m（玩家看不出差别）：" + fine.dev, () =>
                        fine.dev <= 0.5);
                    Check.True("C9 精细度往下走 ⇒ 节点数单调不增（省节点是这一档的作用，不是随机行为）："
                        + fine.nodes + "/" + mid.nodes + "/" + coarse.nodes, () =>
                        fine.nodes >= mid.nodes && mid.nodes >= coarse.nodes && coarse.nodes >= 4);
                    Check.True("C9 省下来的节点要付偏差，但偏差有上限：默认档也不许切出超过 2 m（产业地块切出去就是吃了人行道）："
                        + coarse.dev, () => coarse.dev <= 2.0);
                    Check.True("C9 行政区域尺度（半径 45 m 的 prefab）比产业地块更省点", () =>
                        Run(dense, AreaTier.District, 45, 0.5).nodes <= mid.nodes);
                    Check.True("C9 任何一档都不得超出节点预算", () =>
                    {
                        var t = PolicyKit.Resolve(ModConfig.CreateDefault(), AreaTier.Lot, 12);
                        return fine.nodes <= t.MaxNodesPerEdge + 2 && mid.nodes <= t.MaxNodesPerEdge + 2
                            && coarse.nodes <= t.MaxNodesPerEdge + 2;
                    });

                    // 抽稀闸（TraceKit.RAW_CAP）：密到 0.5 米一格的 629 点原始点列必须照样在
                    // 常数量级时间里出结果 —— 没有这道闸时 RDP 是 O(n²)，节点提交那一帧会卡住。
                    var ultraDense = Arc(200, 0.5);
                    Check.True("C9 原始点列超上限（629 点）时仍然出得来、且守住节点预算", () =>
                    {
                        var r = Run(ultraDense, AreaTier.Lot, 12, 1.0);
                        return r.nodes <= ModConfig.HARD_NODE_CAP + 2 && r.nodes >= 4 && r.dev <= 1.0;
                    });
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    Run(ultraDense, AreaTier.Lot, 12, 0.0);
                    Run(ultraDense, AreaTier.Lot, 12, 0.5);
                    sw.Stop();
                    Check.True("C9 两条超密描边的总耗时 < 1 秒（闸门失效时这里会红，而不是等实机卡帧）："
                        + sw.ElapsedMilliseconds + " ms", () => sw.ElapsedMilliseconds < 1000);
                });

                Harness.Section("TraceKit 与既有区域边界重叠 ⇒ 照抄它的节点坐标（第六轮反馈 7）（T10）", () =>
                {
                    // 玩家原话：「这部分自动放置的节点跟那个区域的节点一致，重叠不赋分不要去判断曲率什么的
                    // 计算节点位置了。」坐标差几厘米 = 两条不重合的边 ⇒ 游戏的放置检查判成物体碰撞。
                    var a = Fix.OnEdge(Fix.XY(10, 0), 0, 10);
                    var b = Fix.OnEdge(Fix.XY(70, 30), 0, 100);

                    // 基准：没有邻接区域时，两个角点就在路的拐角上（否则后面那几条断言是原地打转）
                    var plain = TraceKit.Trace(a, b, Zed(), cfg, Fix.TraceTuning(AreaTier.District), Fix.EmptyCtx());
                    Check.Int("T10 基准：Z 形路交出两个拐角 pin", 2, plain.Points.Count);
                    Check.True("T10 基准：拐角就在 (40,0) 与 (40,30)", () =>
                        HasNear(plain.Points, Fix.XY(40, 0)) && HasNear(plain.Points, Fix.XY(40, 30)));

                    // ① 邻接区域的边界：同一条 Z 形路，但两个角点各偏了 0.4 米（两块地各自描边的正常误差）
                    var w1 = Zed();
                    var nb = new Polyline();
                    nb.Add(Fix.XY(10, 0.4));
                    nb.Add(Fix.XY(40.4, 0.4));
                    nb.Add(Fix.XY(40.4, 30.4));
                    nb.Add(Fix.XY(70, 30.4));
                    w1.Borders.Add(Fix.Border(900, AreaTier.District, nb));
                    var r1 = TraceKit.Trace(a, b, w1, cfg, Fix.TraceTuning(AreaTier.District), Fix.EmptyCtx());
                    Check.Enum("T10 有邻接边界时仍然正常描边（没有被自检拦下）", TraceFallback.None, r1.Fallback);
                    Check.Int("T10 角点认亲不改变点数", 2, r1.Points.Count);
                    Check.True("T10 两个角点的坐标与邻接区域的节点**逐位相同**（距离恰好 0，不是「差一点点」）", () =>
                        r1.Points[0].DistanceTo(nb.Points[1]) == 0 && r1.Points[1].DistanceTo(nb.Points[2]) == 0);
                    Check.True("T10 抄过来的时候连高度一起抄（H 也是坐标的一部分）", () =>
                        r1.Points[0].H == nb.Points[1].H && r1.Points[1].H == nb.Points[2].H);
                    // 这个数会被 GameSide 累进 SnapperState.AdoptedBorderNodes，打进统计行的 weld= 那一项：
                    // 实机验收「共线段有没有取齐」只有它可看（引擎层没有日志器）。
                    Check.Int("T10 认亲的格数被报出来（实机靠统计行的 weld= 判它有没有生效）", 2, r1.Adopted);
                    Check.Int("T10 基准那一次没有邻接边界 ⇒ 认亲数为 0", 0, plain.Adopted);

                    // ② 邻接区域比我们密：它在那段竖路上多两格 ⇒ 补进来，否则端点相同、中间不同，仍不是同一条边
                    var w2 = Zed();
                    var dense = new Polyline();
                    dense.Add(Fix.XY(10, 0.4));
                    dense.Add(Fix.XY(40.4, 0.4));
                    dense.Add(Fix.XY(40.4, 10.4));
                    dense.Add(Fix.XY(40.4, 20.4));
                    dense.Add(Fix.XY(40.4, 30.4));
                    dense.Add(Fix.XY(70, 30.4));
                    w2.Borders.Add(Fix.Border(901, AreaTier.District, dense));
                    var r2 = TraceKit.Trace(a, b, w2, cfg, Fix.TraceTuning(AreaTier.District), Fix.EmptyCtx());
                    Check.Enum("T10 补点之后仍然不回退", TraceFallback.None, r2.Fallback);
                    Check.Int("T10 邻接区域中间那两格被补进来（2 → 4）", 4, r2.Points.Count);
                    Check.True("T10 补进来的每一格都与邻接区域的对应节点逐位相同", () =>
                    {
                        for (int i = 0; i < 4; i++) if (r2.Points[i].DistanceTo(dense.Points[i + 1]) != 0) return false;
                        return true;
                    });
                    Check.True("T10 补点没有被「点数超预算 ⇒ 退回直线」那条闸误杀（预算按补进来的个数放宽）", () =>
                        r2.Points.Count <= Fix.TraceTuning(AreaTier.District).MaxNodesPerEdge + 4);
                    Check.Int("T10 认亲数 = 换掉的 2 格 + 补进来的 2 格", 4, r2.Adopted);

                    // ③ 横穿过去的那条街上的节点：离得很近但方向垂直 ⇒ 不许认亲
                    //（十字路口的四块地各有各的边界，认错了就是把节点拽到马路对面去）
                    var w3 = Zed();
                    var cross = new Polyline();
                    cross.Add(Fix.XY(20, 0.3));
                    cross.Add(Fix.XY(40.3, 0.3));
                    cross.Add(Fix.XY(60, 0.3));
                    w3.Borders.Add(Fix.Border(902, AreaTier.District, cross));
                    var r3 = TraceKit.Trace(a, b, w3, cfg, Fix.TraceTuning(AreaTier.District), Fix.EmptyCtx());
                    Check.Int("T10 垂直方向的邻接边界不参与认亲：点数不变", 2, r3.Points.Count);
                    Check.True("T10 垂直方向的邻接边界不参与认亲：坐标还在路的拐角上", () =>
                        HasNear(r3.Points, Fix.XY(40, 0)) && HasNear(r3.Points, Fix.XY(40, 30)));

                    // ④ 离得太远（> 2 米容差）也不认：那是另一段路上的边界，不是共线
                    var w4 = Zed();
                    var far = new Polyline();
                    far.Add(Fix.XY(10, 6));
                    far.Add(Fix.XY(46, 6));
                    far.Add(Fix.XY(46, 36));
                    far.Add(Fix.XY(70, 36));
                    w4.Borders.Add(Fix.Border(903, AreaTier.District, far));
                    var r4 = TraceKit.Trace(a, b, w4, cfg, Fix.TraceTuning(AreaTier.District), Fix.EmptyCtx());
                    Check.True("T10 隔着 6 米的边界不算重叠（容差 2 米），坐标保持自己描出来的", () =>
                        HasNear(r4.Points, Fix.XY(40, 0)) && HasNear(r4.Points, Fix.XY(40, 30)));
                });

                Harness.Section("TraceKit 路缘档绝不沿中心线描边（第六轮反馈 9/10 的底层修正）（T11）", () =>
                {
                    // 形状：两端都吸在**路口中心**（Side=CENTRE）的产业区。老口径这时会走 LineFor(CENTRE)=中心线，
                    // 于是地块边界沿着马路中间跑，半条马路被划进地块 —— 玩家的原话是
                    // 「只限制在道路/轨道边缘（即两侧人行道边缘，不是路缘）…不包括道路/轨道中心点」。
                    // 新口径：先用区域质心定侧；连质心都没有就不描边（宁可退回直线）。
                    var w = new WorldSnapshot();
                    w.Nodes.Add(Fix.Node(0, 0, 0));
                    w.Nodes.Add(Fix.Node(1, 100, 0));
                    var cl = new Polyline();
                    var ll = new Polyline();
                    var rl = new Polyline();
                    for (int i = 0; i <= 40; i++)
                    {
                        double x = i * 2.5;
                        double y = 12.0 * Math.Sin(Math.PI * x / 100.0);
                        cl.Add(Fix.XY(x, y));
                        ll.Add(Fix.XY(x, y + 6));
                        rl.Add(Fix.XY(x, y - 6));
                    }
                    var e = Fix.Edge(0, NetKind.Road, 0, 1, cl);
                    e.LeftLine = ll;
                    e.RightLine = rl;
                    e.RoadWidth = 12;
                    w.Edges.Add(e);
                    w.BuildAdjacency();

                    var lot = Fix.Tun(AreaTier.Lot, 30, 0.5, 60, 2.0, 3.0);
                    var a = Fix.AtNode(Fix.XY(0, 0), 0, 0, 0);
                    var b = Fix.AtNode(Fix.XY(100, 0), 1, 0, 100);

                    // ① 没有质心 ⇒ 判不出该贴哪一侧 ⇒ 一个点都不描（绝不悄悄用中心线顶上）
                    var noCtx = TraceKit.Trace(a, b, w, cfg, lot, Fix.EmptyCtx());
                    Check.Enum("T11 路缘档 + 两端都在路口中心 + 没有质心 ⇒ 不描边", TraceFallback.NotOnNetwork, noCtx.Fallback);
                    Check.Int("T11 不描边时不交付任何点", 0, noCtx.Points.Count);

                    // ② 质心在北边 ⇒ 贴北侧那条外沿（y = 12·sin(πx/100) + 6），不是中心线
                    var north = new TraceContext { HasCentroid = true, Centroid = Fix.XY(50, 40) };
                    var rN = TraceKit.Trace(a, b, w, cfg, lot, north);
                    Check.Enum("T11 有质心 ⇒ 正常描边", TraceFallback.None, rN.Fallback);
                    Check.True("T11 弯道上有补点（否则这条断言验不到几何）", () => rN.Points.Count >= 3);
                    Check.True("T11 中间的点都压在**北侧外沿**上（离中心线整整 6 米）；"
                        + "首末两格是从路口中心的锚点接到外沿的过渡点，不在缘线上是正常的", () =>
                    {
                        for (int i = 1; i + 1 < rN.Points.Count; i++)
                        {
                            P3 p = rN.Points[i];
                            double centre = 12.0 * Math.Sin(Math.PI * p.X / 100.0);
                            if (Math.Abs(p.Y - (centre + 6)) > 1e-6) return false;
                        }
                        return true;
                    });

                    // ③ 质心在南边 ⇒ 同一条路改贴南侧外沿（判侧跟着质心走，不是写死左侧）
                    var south = new TraceContext { HasCentroid = true, Centroid = Fix.XY(50, -40) };
                    var rS = TraceKit.Trace(a, b, w, cfg, lot, south);
                    Check.Enum("T11 质心在南 ⇒ 照样正常描边", TraceFallback.None, rS.Fallback);
                    Check.True("T11 中间的点都压在**南侧外沿**上（质心在南 ⇒ 判侧跟着翻，不是写死左侧）", () =>
                    {
                        if (rS.Points.Count < 3) return false;
                        for (int i = 1; i + 1 < rS.Points.Count; i++)
                        {
                            P3 p = rS.Points[i];
                            double centre = 12.0 * Math.Sin(Math.PI * p.X / 100.0);
                            if (Math.Abs(p.Y - (centre - 6)) > 1e-6) return false;
                        }
                        return true;
                    });

                    // ④ 中心线档（市辖区）不受这道闸影响：两端在路口中心时它就**该**沿中心线走
                    var dis = Fix.Tun(AreaTier.District, 30, 0.5, 60, 2.0, 3.0);
                    var rD = TraceKit.Trace(a, b, w, cfg, dis, Fix.EmptyCtx());
                    Check.Enum("T11 市辖区照旧沿中心线（这道闸只管路缘档）", TraceFallback.None, rD.Fallback);
                    Check.True("T11 市辖区的点压在中心线上（y = 12·sin）", () =>
                    {
                        for (int i = 0; i < rD.Points.Count; i++)
                        {
                            P3 p = rD.Points[i];
                            if (Math.Abs(p.Y - 12.0 * Math.Sin(Math.PI * p.X / 100.0)) > 1e-6) return false;
                        }
                        return true;
                    });
                });
            }

            /// <summary>
            /// Z 形单折线路：(0,0)→(40,0)→(40,30)→(80,30)，按 10 米采样、路宽 20。
            /// 第六轮反馈 1 之后直线夹具的 Points 为空，「路由/顺序/方向」类检验
            /// 必须用这种带真角的夹具（两个 90° 拐角都是交点 ⇒ 各留一个 pin）。
            /// </summary>
            private static WorldSnapshot Zed()
            {
                var zl = new Polyline();
                var s1 = Fix.Straight(Fix.XY(0, 0), Fix.XY(40, 0), 4);
                var s2 = Fix.Straight(Fix.XY(40, 0), Fix.XY(40, 30), 3);
                var s3 = Fix.Straight(Fix.XY(40, 30), Fix.XY(80, 30), 4);
                for (int i = 0; i < s1.Count; i++) zl.Add(s1.Points[i]);
                for (int i = 1; i < s2.Count; i++) zl.Add(s2.Points[i]);
                for (int i = 1; i < s3.Count; i++) zl.Add(s3.Points[i]);
                var w = new WorldSnapshot();
                w.Nodes.Add(Fix.Node(0, 0, 0));
                w.Nodes.Add(Fix.Node(1, 80, 30));
                var e = Fix.Edge(0, NetKind.Road, 0, 1, zl);
                e.RoadWidth = 20;
                e.Length = zl.Length2D();
                w.Edges.Add(e);
                w.BuildAdjacency();
                return w;
            }

            /// <summary>四分之一圆弧走廊，按 step 米密采（与游戏侧 WorldSampler 同口径）。</summary>
            private static Polyline Arc(double r, double step)
            {
                int n = (int)Math.Round(Math.PI * 0.5 * r / step);
                var line = new Polyline();
                for (int i = 0; i <= n; i++)
                {
                    double a = (Math.PI * 0.5) * i / n;
                    line.Add(new P3(r * Math.Cos(a), r * Math.Sin(a), 0));
                }
                return line;
            }

            /// <summary>把 a/b 挂在同一条弧形边的两端，跑一次描边，数节点、量偏差。<paramref name="detail"/> = 曲线精细度 0..1。</summary>
            private static (int nodes, double dev) Run(Polyline dense, AreaTier tier, double prefabSnapDistance, double detail)
            {
                var cfg = ModConfig.CreateDefault();
                cfg.CurveDetail = detail;
                var tuning = PolicyKit.Resolve(cfg, tier, prefabSnapDistance);
                // 需求 3 之后「锚点贴在哪条线上」决定了描边走哪条线：路缘类的中心线锚点会被
                // IsTraceable 直接判成「不在道路上」⇒ 不描边。所以这条夹具必须按类别造对应档的锚点，
                // 否则测的就不是密度而是「有没有走到密度那一步」。
                bool edgeTier = PolicyKit.UsesEdgeTargets(tier);

                var w = new WorldSnapshot();
                w.Nodes.Add(new GraphNode { Id = 0, Pos = dense.Points[0], Signature = 11 });
                w.Nodes.Add(new GraphNode { Id = 1, Pos = dense.Points[dense.Count - 1], Signature = 12 });
                w.Edges.Add(new GraphEdge
                {
                    Id = 0,
                    Kind = NetKind.Road,
                    StartNode = 0,
                    EndNode = 1,
                    Line = dense,
                    LeftLine = edgeTier ? dense : null,      // 外沿档：把同一条曲线当左缘，密度口径才可比
                    RoadWidth = 20,
                    Length = dense.Length2D(),
                    Signature = 13,
                });
                w.BuildAdjacency();

                var a = edgeTier ? Fix.OnSide(dense.Points[0], 0, 0, PlacedNode.SIDE_LEFT)
                                 : Fix.OnEdge(dense.Points[0], 0, 0);
                var b = edgeTier ? Fix.OnSide(dense.Points[dense.Count - 1], 0, dense.Length2D(), PlacedNode.SIDE_LEFT)
                                 : Fix.OnEdge(dense.Points[dense.Count - 1], 0, dense.Length2D());
                var r = TraceKit.Trace(a, b, w, cfg, tuning, Fix.EmptyCtx());

                var chain = new Polyline();
                chain.Add(a.Pos);
                for (int i = 0; i < r.Points.Count; i++) chain.Add(r.Points[i]);
                chain.Add(b.Pos);

                double maxDev = 0;
                double arcLen = dense.Length2D();
                for (int i = 0; i <= 600; i++)
                {
                    P3 q = GeoKit.PointAtArcLength(dense, arcLen * i / 600.0, out double _);
                    P3 best; int si; double st, al;
                    double d = GeoKit.ClosestPointOnPolylineProbe(chain, q, out best, out si, out st, out al);
                    if (d > maxDev) maxDev = d;
                }
                return (chain.Count, maxDev);
            }

            /// <summary>结果里是否存在某个「离 target 不到 1 毫米」的点（拐角保没保住就用这个问）。</summary>
            private static bool HasNear(List<P3> pts, P3 target)
            {
                if (pts == null) return false;
                for (int i = 0; i < pts.Count; i++) if (pts[i].DistanceTo(target) < 1e-3) return true;
                return false;
            }

            /// <summary>同坐标的点出现了几个（「切换处与交点重合时不许变成两格」用这个数）。</summary>
            private static int CountNear(List<P3> pts, P3 target)
            {
                if (pts == null) return 0;
                int n = 0;
                for (int i = 0; i < pts.Count; i++) if (pts[i].DistanceTo(target) < 1e-3) n++;
                return n;
            }

            /// <summary>只改曲线精细度的一档配置。</summary>
            private static ModConfig Detail(double d)
            {
                var c = ModConfig.CreateDefault();
                c.CurveDetail = d;
                return c;
            }

            /// <summary>每个点都必须贴在某条网络折线上（斜穿街区=原模组那个「边界不沿路」的毛病）。</summary>
            private static bool AllNearNetwork(List<P3> pts, WorldSnapshot w, double tol)
            {
                if (pts == null) return false;
                for (int i = 0; i < pts.Count; i++)
                {
                    bool ok = false;
                    for (int e = 0; e < w.Edges.Count; e++)
                    {
                        var line = w.Edges[e].Line;
                        if (line == null || line.Count < 2) continue;
                        if (Harness.DistToLine(line, pts[i]) <= tol) { ok = true; break; }
                    }
                    if (!ok) return false;
                }
                return pts.Count > 0;
            }
        }
    }
}
