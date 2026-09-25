using System;
using System.Collections.Generic;
using ZoneSnapper.Engine;

namespace ZoneSnapper.T3
{
    internal static partial class Tests
    {
        /// <summary>
        /// SnapKit：需求 4/7 的全部手感都落在这里。玩家点下去 1 厘米都没动 = 白画；
        /// 贴到不该贴的东西上（把半条马路划进地块、把行政区拽到隔壁区界上）= 区域直接画废。
        /// 所以「谁赢」必须逐条钉住，而不是等实机手感。
        /// </summary>
        internal static class Snap
        {
            public static void Run()
            {
                var fx = Fix.SnapFixture();
                var world = fx.World;
                // 夹具的探针是按 60 米半径摆的（旧口径 prefab 20 × 统一 1.5 × 行政区加成 2）。
                // 第五轮反馈 3 把那三层相乘删了 ⇒ 现在只有「每类一条滑杆」，于是这里直接把市辖区调到 300%。
                var cfg = ModConfig.CreateDefault();
                cfg.DistrictSnapDistance = 3.0;
                var tuning = PolicyKit.Resolve(cfg, AreaTier.District, 20);
                var none = Fix.Free(Fix.XY(0, 0));   // Kind=Free 的游戏吸附结果 = 「游戏自己没贴上」

                Harness.Section("SnapKit 距离与分级（N）", () =>
                {
                    Check.Close("N 前置：prefab 20 × 该档滑杆 3.0 ⇒ 半径 60", 60, tuning.SnapRadius, 1e-9);

                    // 最近的道路赢过较远的区域边界
                    var r = SnapKit.FindSnap(Fix.XY(150, 8), world, cfg, tuning, none);
                    Check.Enum("N 近处的中心线赢过远处同类的区域边界", SnapKind.NetCentre, r.Kind);
                    Check.True("N 吸附位置 = (150,0)", () => Harness.Near(r.Pos, Fix.XY(150, 0), 1e-9));
                    Check.Int("N 记住贴在哪条边（描边要用）", fx.E2, r.Edge);
                    Check.Close("N 记住弧长（同边切子段要用）", 50, r.Arc, 1e-9);
                    Check.Int("N 中心线候选没有图节点号", -1, r.GraphNode);

                    // 反过来：边界更近时边界赢（同类优先，品红档）
                    var r2 = SnapKit.FindSnap(Fix.XY(178, 30), world, cfg, tuning, none);
                    Check.Enum("N 边界更近 ⇒ 贴到已有区域边界（Magenta 档）", SnapKind.AreaBorderSame, r2.Kind);
                    Check.Int("N 区域边界的边号用负数编码 -(b+1)", -(fx.BorderDistrict + 1), r2.Edge);
                    Check.True("N 区域边界吸附位置 = (180,30)", () => Harness.Near(r2.Pos, Fix.XY(180, 30), 1e-9));

                    // 半径就是道闸：1 米之差必须从「贴上」翻成「自由」（(30,±59) 处只有中心线一个候选，翻盘因素已排除）
                    var inR = SnapKit.FindSnap(Fix.XY(30, 59), world, cfg, tuning, none);
                    var outR = SnapKit.FindSnap(Fix.XY(30, 61), world, cfg, tuning, none);
                    Check.Enum("N 半径内（59 米）⇒ 贴上", SnapKind.NetCentre, inR.Kind);
                    Check.Enum("N 半径外（61 米）⇒ 一律不贴", SnapKind.Free, outR.Kind);
                    Check.True("N 不贴时位置就是玩家点的原点（一厘米都不许挪）", () => Harness.Near(outR.Pos, Fix.XY(30, 61), 1e-12));
                    var far = SnapKit.FindSnap(Fix.XY(1000, 1000), world, cfg, tuning, none);
                    Check.Enum("N 旷野里什么都不贴", SnapKind.Free, far.Kind);
                    Check.True("N 旷野里 GraphNode/Edge 都是 -1", () => far.GraphNode == -1 && far.Edge == -1);
                    Check.True("N PlacedNode 永远带可用位置（结构体不可能是 null，但落点必须是原始点）", () => far.Pos.DistanceTo(Fix.XY(1000, 1000)) == 0);
                });

                Harness.Section("SnapKit 交叉口与已有边界的分级（N2）", () =>
                {
                    // 第五轮反馈 5 删掉了「优先交叉口 / 优先已有边界」这两个开关：
                    // 前者变成写死的分级（交叉口 0.7，仍比中心线便宜，但不再是玩家可调的加成），
                    // 后者整条没了 —— 已有区域边界永远允许吸（反馈 4 要的是能力）。
                    // 所以这一组现在钉的是**分级本身**，而不是开关切换。
                    var raw = Fix.XY(90, -20);
                    var none2 = Fix.Free(raw);

                    // 交叉口 (100,0) 距离 22.36 → 22.36×0.7=15.65；中心线 (90,0) 距离 20 → 20×1.0=20
                    var withBonus = SnapKit.FindSnap(raw, world, cfg, tuning, none2);
                    Check.Enum("N2 交叉口分级 0.7 ⇒ 更近的中心线输给交叉口", SnapKind.NetNode, withBonus.Kind);
                    Check.Int("N2 贴到的是那个度 3 的交叉口", fx.NJunction, withBonus.GraphNode);

                    // 分级加成是有界的：0.7 只能覆盖 1/0.7≈1.43 倍的距离差，再近的中心线照样赢。
                    // （旧版靠 IntersectionBonus=1.0 来验这条，现在它是常量，只能靠几何翻盘。）
                    var nearCentre = SnapKit.FindSnap(Fix.XY(90, -4), world, cfg, tuning, Fix.Free(Fix.XY(90, -4)));
                    Check.Enum("N2 中心线明显更近（4 米 vs 交叉口 14.4 米）⇒ 交叉口那点加成翻不回来", SnapKind.NetCentre, nearCentre.Kind);
                    Check.True("N2 分级比 = 0.7 ⇒ 交叉口最多赢 1.43 倍距离差", () =>
                        Harness.Near(1.0 / PolicyKit.KindWeight(cfg, SnapKind.NetNode), 1.0 / 0.7, 1e-9));

                    // 已有区域边界：不再有开关，但也不能抢走它前面的交叉口。
                    // 交叉口 (100,0) 三条路全往北/东/西，玩家点在它南边 10 米处；同类边界在 y=-22（12 米）。
                    var w2 = new WorldSnapshot();
                    w2.Nodes.Add(Fix.Node(0, 100, 0));
                    w2.Nodes.Add(Fix.Node(1, 100, 40));
                    w2.Nodes.Add(Fix.Node(2, 140, 0));
                    w2.Nodes.Add(Fix.Node(3, 60, 0));
                    w2.Edges.Add(Fix.Edge(0, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(100, 0), Fix.XY(100, 40), 4)));
                    w2.Edges.Add(Fix.Edge(1, NetKind.Road, 0, 2, Fix.Straight(Fix.XY(100, 0), Fix.XY(140, 0), 4)));
                    w2.Edges.Add(Fix.Edge(2, NetKind.Road, 3, 0, Fix.Straight(Fix.XY(60, 0), Fix.XY(100, 0), 4)));
                    w2.Borders.Add(Fix.Border(9, AreaTier.District, Fix.Poly(50, -22, 150, -22)));
                    w2.BuildAdjacency();
                    Check.Int("N2 前置：这是个度 3 的真交叉口", 3, w2.Nodes[0].Degree);
                    var rawC = Fix.XY(100, -10);
                    var tD = PolicyKit.Resolve(cfg, AreaTier.District, 20);
                    var rC = SnapKit.FindSnap(rawC, w2, cfg, tD, Fix.Free(rawC));
                    // 交叉口距离 10（×0.7 ⇒ 7）；中心线距离 10（×1.0 ⇒ 10）；边界距离 12（×0.85 ⇒ 10.2）
                    Check.Enum("N2 交叉口与中心线等距时交叉口赢，且也压过稍远的同类边界", SnapKind.NetNode, rC.Kind);
                    var rawFar = Fix.XY(60, -14);     // 离交叉口 42.4 米、离边界 8 米
                    var rBorder = SnapKit.FindSnap(rawFar, w2, cfg, tD, Fix.Free(rawFar));
                    Check.Enum("N2 没有更近的交叉口来抢时，同类区域边界照样能吸（反馈 4：这是能力，不是开关）",
                        SnapKind.AreaBorderSame, rBorder.Kind);
                    Check.True("N2 落点真的在那条边界上（y=-22）", () => Harness.Near(rBorder.Pos, Fix.XY(60, -22), 1e-9));
                });

                Harness.Section("SnapKit 贴合目标按类别分档（N3，需求 3）", () =>
                {
                    var raw = Fix.XY(150, -6);       // 正落在路缘线上，离中心线 6 米
                    var lotCfg = ModConfig.CreateDefault();
                    var lotTuning = PolicyKit.Resolve(lotCfg, AreaTier.Lot, 20);

                    // 同一个点、同一条路：市辖区贴中心线，地块贴路缘 —— 这正是需求 3 要的分档
                    var asDistrict = SnapKit.FindSnap(raw, world, cfg, tuning, Fix.Free(raw));
                    var asLot = SnapKit.FindSnap(raw, world, lotCfg, lotTuning, Fix.Free(raw));
                    Check.Enum("N3 市辖区 ⇒ 只贴中心线（游戏给 District 的是 Snap.NetMiddle）", SnapKind.NetCentre, asDistrict.Kind);
                    Check.Enum("N3 地块 ⇒ 贴路缘（游戏给 Lot 的是 Snap.NetSide|ObjectSide）", SnapKind.NetSide, asLot.Kind);
                    Check.Int("N3 路缘候选仍记住所属边", fx.E2, asLot.Edge);
                    Check.Bool("N3 落在 y=-6 ⇒ 认得自己是贴了**右**侧缘（跨侧判据要用）",
                        true, asLot.Side == PlacedNode.SIDE_RIGHT);
                    Check.True("N3 路缘档的落点就在边线上（6 米偏差不许缩水到中心线）",
                        () => Harness.Near(asLot.Pos, Fix.XY(150, -6), 1e-9));

                    // 路缘档拿不到时**绝不回落中心线**（反馈 3：产业区吸的是人行道外缘，
                    // 把地块角点吸到马路中心 = 半条马路划进地块，比不吸更错）。
                    // 第五轮把「允许贴路缘」那个开关删了 ⇒ 这里改成用隧道造一个「拿不到路缘」的场景，
                    // 判的还是同一件事：这一档宁可不贴。
                    var offCfg = ModConfig.CreateDefault();
                    offCfg.Enabled = false;                       // 只用来验「关掉时一个候选都不生成」
                    var offTuning = PolicyKit.Resolve(offCfg, AreaTier.Lot, 20);
                    var off = SnapKit.FindSnap(raw, world, offCfg, offTuning, Fix.Free(raw));
                    Check.True("N3 总开关关掉 ⇒ 这一档什么都不贴，而不是回落到中心线",
                        () => off.Kind == SnapKind.Free);
                    Check.True("N3 关掉后落点就是玩家原始点（一厘米都不许挪）",
                        () => Harness.Near(off.Pos, raw, 1e-12));
                    var edgeTuning = PolicyKit.Resolve(ModConfig.CreateDefault(), AreaTier.Lot, 20);
                    var edgeOnly = SnapKit.FindSnap(raw, world, ModConfig.CreateDefault(), edgeTuning, Fix.Free(raw));
                    Check.True("N3 开着时地块确实贴到路缘（而不是被中心线档截胡）",
                        () => edgeOnly.Kind == SnapKind.NetSide);

                    // 市辖区永远收不到路缘候选：不是权重压的，是根本不进打分器
                    Check.Bool("N3 KindAllowed(District, NetCentre)", true, PolicyKit.KindAllowed(cfg, AreaTier.District, SnapKind.NetCentre));
                    Check.Bool("N3 KindAllowed(District, NetSide) 为假", false, PolicyKit.KindAllowed(cfg, AreaTier.District, SnapKind.NetSide));
                    Check.Bool("N3 KindAllowed(Lot, NetSide)", true, PolicyKit.KindAllowed(cfg, AreaTier.Lot, SnapKind.NetSide));
                    Check.Bool("N3 KindAllowed(Lot, NetCentre) 为假（地块不能吸路中心）", false, PolicyKit.KindAllowed(cfg, AreaTier.Lot, SnapKind.NetCentre));
                    Check.Bool("N3 两档都收建筑轮廓，市辖区档不收", true,
                        PolicyKit.KindAllowed(cfg, AreaTier.Surface, SnapKind.ObjectSide)
                        && !PolicyKit.KindAllowed(cfg, AreaTier.District, SnapKind.ObjectSide));
                    // 反馈 5：「允许贴路缘」开关删除 ⇒ 不带类别的老口径只剩总开关这一道，恒为真。
                    Check.Bool("N3 老口径 KindAllowed(NetSide) 只看总开关：开着就为真", true, PolicyKit.KindAllowed(cfg, SnapKind.NetSide));
                    Check.Bool("N3 老口径不再被任何类别偏好影响", true, PolicyKit.KindAllowed(offCfg, SnapKind.NetSide) == false);
                    Check.Bool("N3 关掉总开关 ⇒ 连中心线档都不允许", false, PolicyKit.KindAllowed(new ModConfig { Enabled = false }, AreaTier.District, SnapKind.NetCentre));
                    Check.Bool("N3 cfg=null ⇒ 一律不允许（不抛）", false, PolicyKit.KindAllowed(null, AreaTier.District, SnapKind.NetCentre));

                    // 隧道：游戏贴路缘时排除隧道，我们同口径（高架不排除，需求 5 要它）
                    var tw = new WorldSnapshot();
                    tw.Nodes.Add(Fix.Node(0, 0, 0)); tw.Nodes.Add(Fix.Node(1, 100, 0));
                    var te = Fix.Edge(0, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(0, 0), Fix.XY(100, 0), 6));
                    te.LeftLine = Fix.Straight(Fix.XY(0, 6), Fix.XY(100, 6), 6);
                    te.RightLine = Fix.Straight(Fix.XY(0, -6), Fix.XY(100, -6), 6);
                    te.Tunnel = true;
                    tw.Edges.Add(te);
                    tw.BuildAdjacency();
                    var tTun = PolicyKit.Resolve(lotCfg, AreaTier.Lot, 20);
                    var rTun = SnapKit.FindSnap(Fix.XY(50, -6), tw, lotCfg, tTun, Fix.Free(Fix.XY(50, -6)));
                    Check.True("N3 隧道不贴边线（与游戏 Snap.NetSide 的 CheckComposition 同口径）",
                        () => rTun.Kind != SnapKind.NetSide);
                    Check.True("N3 隧道那里也不许退回中心线（地块角点吸到路中心更错）",
                        () => rTun.Kind != SnapKind.NetCentre && rTun.Kind == SnapKind.Free);
                    te.Tunnel = false;
                    te.Elevated = true;
                    var rElev = SnapKit.FindSnap(Fix.XY(50, -6), tw, lotCfg, tTun, Fix.Free(Fix.XY(50, -6)));
                    Check.Enum("N3 高架照常贴（需求 5）", SnapKind.NetSide, rElev.Kind);
                });

                Harness.Section("SnapKit 与游戏自带吸附同场竞技（N4）", () =>
                {
                    // 这一组用市辖区档：中心线类别本来就拿不到路缘候选（KindAllowed 的类别闸），
                    // 不需要再关任何开关，(150,-6) 那条路缘不会截胡。
                    var cfgN = ModConfig.CreateDefault();
                    cfgN.DistrictSnapDistance = 3.0;
                    var tN = PolicyKit.Resolve(cfgN, AreaTier.District, 20);
                    // (i) 游戏给的点更划算 ⇒ 用它，且 kind/entity/arc/signature 原样带回来（描边要用）
                    var game = new PlacedNode(Fix.XY(100, 0), SnapKind.NetNode, fx.NJunction, fx.E2, 33, 4242);
                    var raw = Fix.XY(100, -11);
                    var r = SnapKit.FindSnap(raw, world, cfgN, tN, game);
                    Check.Enum("N4 游戏的交叉口候选一起打分并能胜出", SnapKind.NetNode, r.Kind);
                    Check.True("N4 胜出时位置用游戏给的那个点", () => Harness.Near(r.Pos, game.Pos, 1e-12));
                    Check.Int("N4 边号沿用游戏候选的（不是我们另算的那个）", fx.E2, r.Edge);
                    Check.Close("N4 弧长沿用游戏候选的", 33, r.Arc, 1e-12);
                    Check.Long("N4 几何签名沿用游戏候选的", 4242, r.Signature);

                    // (ii) 我们的候选更划算 ⇒ 游戏的点不能强行胜出
                    //     （v0.1.4：原来这一条用海岸线档当「更差的候选」，那一档整条删了 ⇒ 换成异类区域边界 1.25）
                    var worse = new PlacedNode(Fix.XY(100, 40), SnapKind.AreaBorderOther, -1, -100, 0, 7);
                    var r2 = SnapKit.FindSnap(Fix.XY(150, 8), world, cfgN, tN, worse);
                    Check.Enum("N4 游戏的点分数更差 ⇒ 我们用自家的中心线", SnapKind.NetCentre, r2.Kind);
                    Check.True("N4 落点不是游戏给的那个", () => !Harness.Near(r2.Pos, worse.Pos, 1e-9));

                    // (iii) 超出 1.5r 的游戏候选直接不看
                    var outside = new PlacedNode(Fix.XY(300, 200), SnapKind.NetNode, 0, 0, 0, 9);
                    var r3 = SnapKit.FindSnap(Fix.XY(300, 300), world, cfgN, tN, outside);
                    Check.Enum("N4 游戏候选远得离谱（>1.5r）⇒ 忽略，玩家点哪是哪", SnapKind.Free, r3.Kind);
                    Check.True("N4 忽略时位置=原始射线点", () => Harness.Near(r3.Pos, Fix.XY(300, 300), 1e-12));

                    // (iv) 落在 1.5r 内但超过 r：照样不能用 ——「超过半径就不算贴上」是最后那道闸
                    var sneaky = new PlacedNode(Fix.XY(300, 230), SnapKind.NetNode, 0, 0, 0, 9);
                    var r4 = SnapKit.FindSnap(Fix.XY(300, 300), world, cfgN, tN, sneaky);
                    Check.Enum("N4 游戏候选在 1.5r 内但超 r ⇒ 仍不贴（半径是硬闸）", SnapKind.Free, r4.Kind);
                    Check.True("N4 不贴时不许用游戏点当落点", () => Harness.Near(r4.Pos, Fix.XY(300, 300), 1e-12));

                    // (v) 关闭吸附时：游戏给的非 Free 点必须原样交出去（不抢游戏的活）
                    var tOff = PolicyKit.Resolve(cfgN, AreaTier.District, 20);
                    tOff.SnapEnabled = false;
                    var r5 = SnapKit.FindSnap(Fix.XY(150, 8), world, cfgN, tOff, game);
                    Check.True("N4 SnapEnabled=false ⇒ 直接交还游戏的吸附结果", () => r5.Kind == game.Kind && Harness.Near(r5.Pos, game.Pos, 1e-12));
                    var r6 = SnapKit.FindSnap(Fix.XY(150, 8), world, cfgN, tOff, Fix.Free(Fix.XY(150, 8)));
                    Check.Enum("N4 SnapEnabled=false 且游戏也没贴上 ⇒ Free", SnapKind.Free, r6.Kind);
                    var r7 = SnapKit.FindSnap(Fix.XY(150, 8), world, cfgN, Fix.Tun(AreaTier.District, 0, 0.5, 60, 2, 3), Fix.Free(Fix.XY(150, 8)));
                    Check.Enum("N4 半径解析成 0 也绝不抛，退化成 Free", SnapKind.Free, r7.Kind);
                    var r8 = SnapKit.FindSnap(Fix.XY(150, 8), world, new ModConfig { Enabled = false }, tN, Fix.Free(Fix.XY(150, 8)));
                    Check.Enum("N4 总开关关掉 ⇒ 一个候选都不生成（交还游戏）", SnapKind.Free, r8.Kind);
                });

                Harness.Section("SnapKit 确定性与平局（N5）", () =>
                {
                    var probes = new[] { Fix.XY(150, 8), Fix.XY(90, -20), Fix.XY(178, 30), Fix.XY(150, -6), Fix.XY(100, 39), Fix.XY(30, 59) };
                    bool all = true;
                    for (int i = 0; i < probes.Length; i++)
                    {
                        var x = SnapKit.FindSnap(probes[i], world, cfg, tuning, Fix.Free(probes[i]));
                        var y = SnapKit.FindSnap(probes[i], world, cfg, tuning, Fix.Free(probes[i]));
                        if (x.Kind != y.Kind || !Harness.Near(x.Pos, y.Pos, 0) || x.Edge != y.Edge
                            || x.GraphNode != y.GraphNode || !Harness.Near(x.Arc, y.Arc, 0) || x.Signature != y.Signature)
                        {
                            Check.Bad("N5 同输入两次调用结果不同（平局没保留先来者）", probes[i].ToString(), x.Kind + "/" + x.Edge + " vs " + y.Kind + "/" + y.Edge);
                            all = false;
                        }
                    }
                    Check.True("N5 " + probes.Length + " 个探针位置各跑两遍，逐字段全等", () => all);

                    // 真平局：两条等距的平行路，先被扫到的（下标小的）赢，结果可复现
                    var w = new WorldSnapshot();
                    w.Nodes.Add(Fix.Node(0, 0, 5)); w.Nodes.Add(Fix.Node(1, 20, 5));
                    w.Nodes.Add(Fix.Node(2, 0, -5)); w.Nodes.Add(Fix.Node(3, 20, -5));
                    w.Edges.Add(Fix.Edge(0, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(0, 5), Fix.XY(20, 5), 4)));
                    w.Edges.Add(Fix.Edge(1, NetKind.Road, 2, 3, Fix.Straight(Fix.XY(0, -5), Fix.XY(20, -5), 4)));
                    w.BuildAdjacency();
                    var t2 = Fix.Tun(AreaTier.District, 30, 0.5, 60, 2, 3);
                    var c2 = ModConfig.CreateDefault();
                    var tie = SnapKit.FindSnap(Fix.XY(10, 0), w, c2, t2, Fix.Free(Fix.XY(10, 0)));
                    Check.Enum("N5 完全平局 ⇒ 贴到扫描顺序里第一条（边 0），不是随机", SnapKind.NetCentre, tie.Kind);
                    Check.Int("N5 平局保留先来者", 0, tie.Edge);
                    var tie2 = SnapKit.FindSnap(Fix.XY(10, 0), w, c2, t2, Fix.Free(Fix.XY(10, 0)));
                    Check.Int("N5 平局再来一次仍是边 0", 0, tie2.Edge);

                    // 度数分层：度 2 的分段点与度 1 的断头点都不该比交叉口更值得贴
                    Check.True("N5 交叉口比同距离的分段点优先", () =>
                    {
                        var t = PolicyKit.KindWeight(cfg, SnapKind.NetNode);
                        return t < 1.0;   // 中心线档恒为 1.0
                    });
                    Check.Close("N5 中心线档权重 1.0", 1.0, PolicyKit.KindWeight(cfg, SnapKind.NetCentre), 1e-12);
                    Check.Close("N5 路缘档现在是路缘类的主档 ⇒ 比中心线略便宜（0.95）",
                        0.95, PolicyKit.KindWeight(cfg, SnapKind.NetSide), 1e-12);
                    Check.Close("N5 建筑轮廓档与中心线档同权（1.0）", 1.0, PolicyKit.KindWeight(cfg, SnapKind.ObjectSide), 1e-12);
                    Check.True("N5 优先级顺序：交叉口 < 同类边界 < 路缘 < 建筑≈中心线 < 异类边界 < 瓦片", () =>
                    {
                        double A = PolicyKit.KindWeight(cfg, SnapKind.NetNode);
                        double B = PolicyKit.KindWeight(cfg, SnapKind.AreaBorderSame);
                        double C = PolicyKit.KindWeight(cfg, SnapKind.NetSide);
                        double O = PolicyKit.KindWeight(cfg, SnapKind.ObjectSide);
                        double M = PolicyKit.KindWeight(cfg, SnapKind.NetCentre);
                        double D = PolicyKit.KindWeight(cfg, SnapKind.AreaBorderOther);
                        double E = PolicyKit.KindWeight(cfg, SnapKind.MapTileBorder);
                        return A < B && B < C && C < O && O == M && M < D && D < E;
                    });
                    // v0.1.4：海岸线档整条删除 ⇒ 它落进 default 的 4.0（谁都别想再靠这个数字捞回它）。
                    Check.Close("N5 已删除的档一律吃 default 权重 4.0（不另立门户）",
                        4.0, PolicyKit.KindWeight(cfg, (SnapKind)1), 1e-12);
                    Check.True("N5 分档闸不是权重：路缘类收不到中心线、中心线类收不到路缘（权重再小也不行）", () =>
                        !PolicyKit.KindAllowed(cfg, AreaTier.Lot, SnapKind.NetCentre)
                        && !PolicyKit.KindAllowed(cfg, AreaTier.District, SnapKind.NetSide)
                        && PolicyKit.KindAllowed(cfg, AreaTier.Lot, SnapKind.NetSide));
                    // 旧的 IntersectionBonus 滑杆（0.05..1.0 夹取）随「贴合优先级」板块一起删除，
                    // 现在交叉口分级是常量 0.7 ⇒ 钉住它别被谁顺手改回「可调」，且不许越过 1.0（那等于反向歧视交叉口）。
                    Check.True("N5 交叉口分级落在 (0,1] 之间：既给加成、又不是玩家可调的滑杆", () =>
                    {
                        foreach (ModConfig c in new[] { ModConfig.CreateDefault(),
                            new ModConfig { Enabled = false }, new ModConfig { Mode = TraceMode.Shortest } })
                        {
                            double w = PolicyKit.KindWeight(c, SnapKind.NetNode);
                            if (!(w > 0 && w <= 1.0)) return false;
                        }
                        return true;
                    });
                    Check.True("N5 分级与模式无关（模式管选路，不管落点评分）", () =>
                    {
                        double a = PolicyKit.KindWeight(ModeCfg(TraceMode.Smart), SnapKind.NetSide);
                        double b = PolicyKit.KindWeight(ModeCfg(TraceMode.NoNetworkSwitch), SnapKind.NetSide);
                        return Harness.Near(a, b, 1e-12);
                    });

                    // 异类边界 vs 同类边界：等距时同类赢（0.85 < 1.25）
                    var w3 = new WorldSnapshot();
                    w3.Borders.Add(Fix.Border(1, AreaTier.District, Fix.Poly(9, -50, 9, 50)));      // 距 raw 9 米，同类
                    w3.Borders.Add(Fix.Border(2, AreaTier.Lot, Fix.Poly(-9, -50, -9, 50)));         // 距 raw 9 米，异类
                    var t3 = PolicyKit.Resolve(cfg, AreaTier.District, 20);
                    var rSame = SnapKit.FindSnap(Fix.XY(0, 0), w3, cfg, t3, Fix.Free(Fix.XY(0, 0)));
                    Check.Enum("N5 等距时同类边界(0.85)压过异类边界(1.25)", SnapKind.AreaBorderSame, rSame.Kind);
                    Check.Int("N5 贴的是同类那条（下标 0 ⇒ 编码 -1）", -1, rSame.Edge);
                });

                Harness.Section("SnapKit 瓦片档（N6）", () =>
                {
                    var w = new WorldSnapshot();
                    w.MapTiles.Add(Fix.Border(1, AreaTier.MapTile, Fix.Poly(0, 0, 0, 100)));
                    var t = Fix.Tun(AreaTier.District, 60, 0.5, 60, 2, 3);
                    var rTile = SnapKit.FindSnap(Fix.XY(-5, 50), w, cfg, t, Fix.Free(Fix.XY(-5, 50)));
                    Check.Enum("N6 地图瓦片边界可贴", SnapKind.MapTileBorder, rTile.Kind);
                    Check.Int("N6 瓦片用 -(1e6+b) 编码，明确不是可描边的网络边", -1000000, rTile.Edge);
                    Check.True("N6 瓦片候选不参与描边（TraceKit 认它 NotOnNetwork）", () =>
                    {
                        var b = SnapKit.FindSnap(Fix.XY(-5, 50), w, cfg, t, Fix.Free(Fix.XY(-5, 50)));
                        var a2 = SnapKit.FindSnap(Fix.XY(-5, 60), w, cfg, t, Fix.Free(Fix.XY(-5, 60)));
                        var tr = TraceKit.Trace(a2, b, w, cfg, t, Fix.EmptyCtx());
                        return tr.Fallback == TraceFallback.NotOnNetwork && tr.Points.Count == 0;
                    });
                    // v0.1.4：「海岸线贴合」整条按玩家要求删除（开关实测看不出效果、意义不大）。
                    // 这里钉住**删干净**：扫一遍这一档的整个候选区间，任何落点都不许再吐出 -(2e6+s) 那种海岸编码。
                    Check.True("N6 海岸线档已删 ⇒ 任何候选都不会带 -(2e6+s) 编码", () =>
                    {
                        for (int x = -30; x <= 30; x += 3)
                        {
                            var n = SnapKit.FindSnap(Fix.XY(x, 50), w, cfg, t, Fix.Free(Fix.XY(x, 50)));
                            if (AnchorCode.IsShore(n.Edge)) return false;
                        }
                        return true;
                    });
                    Check.Empty("N6 空世界的候选表确实是空的", new List<P3>());
                    var rEmpty = SnapKit.FindSnap(Fix.XY(5, 5), new WorldSnapshot(), cfg, t, Fix.Free(Fix.XY(5, 5)));
                    Check.Enum("N6 全新世界 ⇒ Free", SnapKind.Free, rEmpty.Kind);
                    var rNull = SnapKit.FindSnap(Fix.XY(5, 5), null, cfg, t, Fix.Free(Fix.XY(5, 5)));
                    Check.Enum("N6 world=null ⇒ Free 不抛", SnapKind.Free, rNull.Kind);
                    var rNullCfg = SnapKit.FindSnap(Fix.XY(150, -6), world, null, tuning, Fix.Free(Fix.XY(150, -6)));
                    Check.True("N6 cfg=null 走默认配置而不是 NRE", () => rNullCfg.Kind == SnapKind.NetSide || rNullCfg.Kind == SnapKind.NetCentre);
                });
            }

            private static ModConfig ModeCfg(TraceMode m)
            {
                var c = ModConfig.CreateDefault();
                c.Mode = m;
                return c;
            }
        }
    }
}
