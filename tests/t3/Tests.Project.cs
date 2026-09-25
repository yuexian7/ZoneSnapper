using System;
using System.Collections.Generic;
using ZoneSnapper.Engine;

namespace ZoneSnapper.T3
{
    internal static partial class Tests
    {
        /// <summary>
        /// ProjectKit：本模组的核心不变量（设计文档 §一 第 5 条推论、§三）。
        ///
        /// 形状必须恒为 [m0, T(m0→m1), m1, T(m1→m2), m2, …, mN]，也就是
        ///   ① 最后一格永远是最后一个手动节点（任何自动点都不许排在它后面）；
        ///   ② 于是游戏自己的 [Length-2]↔[Length-1] 最小间距判定仍然是「上一手动节点→光标」，
        ///      我们一行游戏代码都不用 patch（FACT：ATS:3509-3511）。
        /// ① 一旦破掉，症状是「玩家点不下去最后一个节点」或「区域莫名其妙闭不上」，
        /// 而且只在特定描边点数下出现 —— 实机极难复现，所以这里用大样本钉死。
        /// </summary>
        internal static class Project
        {
            public static void Run()
            {
                Harness.Section("ProjectKit 投影不变量（P）", () =>
                {
                    var cfg = ModConfig.CreateDefault();
                    var tuning = Fix.Tun(AreaTier.District, 60, 0.5, 60, 2.0, 3.0);

                    // 0/1/2/5 个手动节点 × 各边描边点数 0、1、17、40、100 的组合，全都要过同一条形状断言
                    int[][] shapes =
                    {
                        new int[0],                                     // 空栈
                        new int[0],                                     // 单节点
                        new int[] { 0 },                                // 两点无边
                        new int[] { 7 },
                        new int[] { 40 },
                        new int[] { 100 },
                        new int[] { 0, 0, 0 },
                        new int[] { 3, 0, 12 },
                        new int[] { 40, 1, 0, 100 },
                        new int[] { 45, 45, 45, 45 },
                    };
                    int[] manualCounts = { 0, 1, 2, 2, 2, 2, 4, 4, 5, 5 };
                    for (int k = 0; k < shapes.Length; k++)
                    {
                        int n = manualCounts[k];
                        var st = Fix.StackWith(n, shapes[k]);
                        string label = "P n=" + n + " traces=[" + string.Join(",", shapes[k]) + "]";
                        AssertShape(label, st, cfg, tuning, null);
                    }

                    // 极端预算：超预算的边被截断，但形状不变量必须仍然成立
                    var tight = Fix.Tun(AreaTier.District, 60, 0.5, 10, 2.0, 3.0);
                    var big = Fix.StackWith(3, new int[] { 100, 40 });
                    var p = ProjectKit.Build(big, cfg, tight);
                    Check.True("P 预算截断 ⇒ 记 Truncated", () => p.Truncated);
                    AssertShape("P 截断后形状不变", big, cfg, tight, new int[] { 10, 10 });

                    // 纯函数：同输入两次 Build 必须逐格相同（需求 5「自动跟随」靠幂等才敢每帧重算）
                    var st2 = Fix.StackWith(4, new int[] { 12, 0, 33 });
                    var pa = ProjectKit.Build(st2, cfg, tuning);
                    var pb = ProjectKit.Build(st2, cfg, tuning);
                    Check.True("P 幂等：两次 Build 逐格一致", () => SameSlots(pa, pb));
                    AssertShape("P 幂等复跑", st2, cfg, tuning, null);

                    // 描边点绝不排在最后手动节点之后 —— 这条就是 §一 第 5 条推论的全部内容与否
                    var tailHeavy = Fix.StackWith(2, new int[] { 100 });
                    var pth = ProjectKit.Build(tailHeavy, cfg, tuning);
                    Check.Int("P 末格下标 = 槽位数-1", pth.Slots.Count - 1, LastManualIndex(pth));
                    Check.True("P 末格是手动节点", () => pth.Slots[pth.Slots.Count - 1].IsManual);
                    Check.Close("P 末格位置 == 最后手动节点位置", 0,
                        pth.Slots[pth.Slots.Count - 1].Pos.DistanceTo(tailHeavy.Nodes[1].Pos), 1e-12);
                });

                Harness.Section("ProjectKit 光标/闭合/写盘判定（P2）", () =>
                {
                    var cfg = ModConfig.CreateDefault();
                    var tuning = Fix.Tun(AreaTier.District, 60, 0.5, 60, 2.0, 3.0);
                    var st = Fix.StackWith(3, new int[] { 5, 5 });
                    var p = ProjectKit.Build(st, cfg, tuning);
                    Check.Int("P2 投影不含游标（游标由游戏自己维护）", 3 + 10, p.Slots.Count);
                    Check.Int("P2 ManualCount+AutoCount == 槽位数", p.Slots.Count, p.ManualCount + p.AutoCount);

                    var pos = p.Positions();
                    Check.False("P2 位置未变 ⇒ 不写盘", () => ProjectKit.NeedsWrite(p, pos, Fix.XY(300, 0), Fix.XY(300, 0), 0.01));
                    Check.True("P2 游标动了 ⇒ 写盘", () => ProjectKit.NeedsWrite(p, pos, Fix.XY(300, 0), Fix.XY(305, 0), 0.01));
                    var moved = new List<P3>(pos);
                    moved[2] = Fix.XY(moved[2].X + 0.5, moved[2].Y);
                    Check.True("P2 中间某格挪了 0.5 米 ⇒ 写盘", () => ProjectKit.NeedsWrite(p, moved, Fix.XY(300, 0), Fix.XY(300, 0), 0.01));
                    var shorter = new List<P3>(pos); shorter.RemoveAt(shorter.Count - 1);
                    Check.True("P2 槽位数不同 ⇒ 写盘", () => ProjectKit.NeedsWrite(p, shorter, Fix.XY(300, 0), Fix.XY(300, 0), 0.01));
                    Check.True("P2 1 厘米以内的抖动不算变化", () =>
                    {
                        var j = new List<P3>(pos);
                        j[4] = Fix.XY(j[4].X + 0.001, j[4].Y);
                        return !ProjectKit.NeedsWrite(p, j, Fix.XY(300, 0), Fix.XY(300, 0), 0.01);
                    });

                    Check.True("P2 3 个手动节点 + 游标点回起点 ⇒ 判闭合", () => ProjectKit.IsClosing(st, st.Nodes[0].Pos, 0.5));
                    Check.False("P2 2 个手动节点不判闭合（游戏自己也要 Length>=4 格）", () =>
                        ProjectKit.IsClosing(Fix.StackWith(2, new int[] { 3 }), Fix.XY(0, 0), 0.5));
                    Check.False("P2 游标离起点远 ⇒ 不闭合", () => ProjectKit.IsClosing(st, Fix.XY(50, 50), 0.5));
                    Check.False("P2 空栈不闭合、不抛", () => ProjectKit.IsClosing(new ManualStack(), Fix.XY(0, 0), 0.5));
                });

                Harness.Section("ProjectKit 写表总闸 MayWrite（P2b，实机 v0.1.0 事故的回归位）", () =>
                {
                    const double EPS = 1.0;
                    var cfg = ModConfig.CreateDefault();
                    var tuning = Fix.Tun(AreaTier.District, 60, 0.5, 60, 2.0, 3.0);

                    // 事故本体：Harmony 没打上 ⇒ 手动栈永远是空 ⇒ 投影 0 槽，而游戏的表里已经有玩家刚点的点。
                    // 那时 NeedsWrite 会为真（长度不同），照写就把玩家的第一个节点抹了。
                    var empty = ProjectKit.Build(new ManualStack(), cfg, tuning);
                    Check.Int("P2b 空栈的投影是 0 槽", 0, empty.Slots.Count);
                    Check.False("P2b 表里有玩家的点、我们从没写过（lastWritten=null）⇒ 不许写（挡住「点不下第一个节点」）", () =>
                        ProjectKit.MayWrite(empty, new List<P3> { Fix.XY(10, 0) }, null, EPS));
                    Check.False("P2b 两个点 + lastWritten 是空表 ⇒ 同样不许", () =>
                        ProjectKit.MayWrite(empty, new List<P3> { Fix.XY(10, 0), Fix.XY(20, 0) }, new List<P3>(), EPS));
                    Check.True("P2b 表是空的（只有游标）⇒ 允许写（这时只挪游标，删不掉任何东西）", () =>
                        ProjectKit.MayWrite(empty, new List<P3>(), null, EPS));
                    Check.True("P2b currentWithoutCursor 为 null 按「无冲突」处理，不炸", () =>
                        ProjectKit.MayWrite(empty, null, null, EPS));
                    Check.False("P2b 投影为 null ⇒ 不写", () => ProjectKit.MayWrite(null, new List<P3>(), null, EPS));

                    var st = Fix.StackWith(3, new int[] { 5, 5 });
                    var p = ProjectKit.Build(st, cfg, tuning);
                    Check.True("P2b 正常形状（我们 13 槽 vs 游戏 3 格）⇒ 加点，可以写", () => ProjectKit.MayWrite(p, new List<P3>
                    {
                        st.Nodes[0].Pos, st.Nodes[1].Pos, st.Nodes[2].Pos
                    }, null, EPS));
                    Check.True("P2b 等长 ⇒ 可以写", () => ProjectKit.MayWrite(p, p.Positions(), null, EPS));
                    // 「比我们多」必须真的比槽位数多：p 是 3 手动 + 10 描边 = 13 槽，写 6 个点反而是我们要减点。
                    var moreThanUs = new List<P3>();
                    for (int i = 0; i <= p.Slots.Count; i++) moreThanUs.Add(Fix.XY(i * 3.0, 0));
                    Check.Int("P2b 反例长度确比我们多 1", p.Slots.Count + 1, moreThanUs.Count);
                    Check.False("P2b 表里多出来的那格不是我们写的 ⇒ 漏收事件，这一帧不写", () =>
                        ProjectKit.MayWrite(p, moreThanUs, p.Positions(), EPS));
                    // 口径本身：只要我们是「加点或等长」，闸就不拦；只有减点才走那份证据
                    for (int extra = 0; extra <= 4; extra++)
                    {
                        var bigger = new List<P3>();
                        for (int i = 0; i < p.Slots.Count + extra; i++) bigger.Add(Fix.XY(i, 0));
                        bool expect = extra == 0;
                        Check.True("P2b 表长 " + bigger.Count + " vs 我们 " + p.Slots.Count + " 的判定符合「只减要查证据」", () =>
                            ProjectKit.MayWrite(p, bigger, null, EPS) == expect);
                    }

                    // ———— 正当减点面：这一组是「只加不减」那版闸的自毁位 ————
                    // 右键撤销的净效果 = 弹掉一个手动节点 **连同那条边的全部描边点**（需求 3），必然要少写几格；
                    // 玩家把「简化程度」往粗调、或关掉走弧线，同理。一刀切禁减 ⇒ 删掉的角还留在表里、
                    // 每帧撞闸、desync 疯涨，最后自愈还会把剩下的描边点当玩家节点收进栈 —— 环彻底乱掉。
                    var ours = p.Positions();
                    var table14 = new List<P3>(ours); table14.Add(Fix.XY(400, 400));   // 14 格，全是我们写的
                    var written14 = new List<P3>(table14);                              // 上一帧我们写进去的那份
                    Check.False("P2b 前置：这确实是减点场景（13 < 14）", () => p.Slots.Count >= table14.Count);
                    Check.True("P2b 表里每一格都出自我们上一帧 ⇒ 放行减点（右键撤销走这条）", () =>
                        ProjectKit.MayWrite(p, table14, written14, EPS));
                    var foreign = new List<P3>(table14); foreign[5] = Fix.XY(9000, 9000);
                    Check.False("P2b 表里混进一格不是我们写的 ⇒ 一格都不许减", () =>
                        ProjectKit.MayWrite(p, foreign, written14, EPS));
                    var nudged = new List<P3>();
                    for (int i = 0; i < table14.Count; i++) nudged.Add(Fix.XY(table14[i].X + 0.5, table14[i].Y));
                    Check.True("P2b 游戏把我们写的点又吸附挪了 0.5 米 ⇒ 仍算我们那一格（eps 要容得下）", () =>
                        ProjectKit.MayWrite(p, nudged, written14, EPS));
                    var movedFar = new List<P3>(table14); movedFar[3] = Fix.XY(movedFar[3].X + 20, movedFar[3].Y);
                    Check.False("P2b 挪出 eps 之外 ⇒ 不当是我们写的，不许减", () =>
                        ProjectKit.MayWrite(p, movedFar, written14, EPS));
                });

                Harness.Section("ProjectKit 漏收提交后的自愈尾巴 MissingTail（P2c）", () =>
                {
                    var cfg = ModConfig.CreateDefault();
                    var tuning = Fix.Tun(AreaTier.District, 60, 0.5, 60, 2.0, 3.0);
                    var st = new ManualStack();
                    st.Push(Fix.Free(Fix.XY(0, 0)));
                    st.Push(Fix.Free(Fix.XY(100, 0)));
                    var p = ProjectKit.Build(st, cfg, tuning);
                    Check.Int("P2c 两个未描边的自由点 ⇒ 投影就两格", 2, p.Slots.Count);

                    Check.Int("P2c 等长 ⇒ 没有尾巴", 0, ProjectKit.MissingTail(p, p.Positions(), st, 0.5).Count);

                    // 玩家多点了一下而我们漏收 ⇒ 尾巴就是那一格，位置原样收下（不重新吸附）
                    var oneMore = p.Positions(); oneMore.Add(Fix.XY(140, 30));
                    var tail1 = ProjectKit.MissingTail(p, oneMore, st, 0.5);
                    Check.Int("P2c 表里多 1 格 ⇒ 收下 1 个", 1, tail1.Count);
                    Check.True("P2c 收的正是玩家点的那一格", () => tail1[0].DistanceTo(Fix.XY(140, 30)) < 1e-9);

                    // 游戏点闭合时会把游标格（= 起点）原样追加成一格 ⇒ 那不是新节点
                    var closing = p.Positions(); closing.Add(Fix.XY(0.02, 0.0));
                    Check.Int("P2c 尾巴与已有手动节点重合 ⇒ 判为重复，不收", 0,
                        ProjectKit.MissingTail(p, closing, st, 0.5).Count);

                    // 一次只救一截：多出一大截说明我们对表的理解错了，不是漏了一两次点击
                    var wayMore = p.Positions();
                    for (int i = 0; i < 20; i++) wayMore.Add(Fix.XY(500 + i, 500 + i));
                    var tailCap = ProjectKit.MissingTail(p, wayMore, st, 0.5);
                    Check.Int("P2c 多出一大截 ⇒ 一次最多收 kMaxRecoverPerStep",
                        ProjectKit.kMaxRecoverPerStep, tailCap.Count);
                    int firstKept = 20 - ProjectKit.kMaxRecoverPerStep;
                    Check.True("P2c 收的是最尾上那几格（不是从中间挑）", () =>
                        tailCap[0].DistanceTo(Fix.XY(500 + firstKept, 500 + firstKept)) < 1e-9);

                    Check.Int("P2c 投影为 null ⇒ 空尾巴不抛", 0, ProjectKit.MissingTail(null, oneMore, st, 0.5).Count);
                    Check.Int("P2c 表为 null ⇒ 空尾巴不抛", 0, ProjectKit.MissingTail(p, null, st, 0.5).Count);
                    Check.Int("P2c 栈为 null ⇒ 只按长度切尾巴，不抛", 1, ProjectKit.MissingTail(p, oneMore, null, 0.5).Count);
                });

                Harness.Section("ProjectKit 落点证据 GameAcceptedCommit（P2d：Apply 事件 ≠ 游戏落了点）", () =>
                {
                    // FACT：ATS:3509-3511 —— State.Create 里 distance(cps[Len-2], cps[Len-1]) < minNodeDistance
                    // 时整段 if 跳过、一个节点都不加（这个 if 没有 else），但 Harmony 后缀照样跑。
                    // 不拦的后果就是把上一个手动节点重复收进栈（回归：右键要按两下才掉一个可见节点）。
                    Check.True("P2d 开局那一击：栈还空 ⇒ 游戏必然落了第一格（Default→Create）", () =>
                        ProjectKit.GameAcceptedCommit(0, 0, 1));
                    Check.True("P2d 还没有写过任何一帧（基线 -1）⇒ 不拦", () =>
                        ProjectKit.GameAcceptedCommit(3, -1, 3));
                    Check.True("P2d 表比我们写的多一格 ⇒ 这一击真落了点", () =>
                        ProjectKit.GameAcceptedCommit(2, 5, 6));
                    Check.False("P2d 表长度没变 ⇒ 游戏把这一击退了，不许重复收点", () =>
                        ProjectKit.GameAcceptedCommit(2, 5, 5));
                    Check.False("P2d 表反而变短（游戏自己清过表）⇒ 同样不算落点", () =>
                        ProjectKit.GameAcceptedCommit(2, 5, 4));
                    // 一格都没写的稳态：只认「严格多出来」，噪声不动手
                    Check.False("P2d 稳态（写 1 格、表还是 1 格）⇒ 拒收", () =>
                        ProjectKit.GameAcceptedCommit(1, 1, 1));
                    Check.True("P2d 稳态下一击落定（写 1 格、表变 2 格）⇒ 收下", () =>
                        ProjectKit.GameAcceptedCommit(1, 1, 2));
                });

                Harness.Section("ProjectKit 闭合边不进实时投影（P3）", () =>
                {
                    var cfg = ModConfig.CreateDefault();
                    var tuning = Fix.Tun(AreaTier.District, 60, 0.5, 60, 2.0, 3.0);
                    // 玩家点了 4 个角（正方形），实时投影只有 m0..m3；闭合边 m3→m0 的描边只在提交时算
                    var st = new ManualStack();
                    st.Push(Fix.Free(Fix.XY(0, 0)));
                    st.Push(Fix.Free(Fix.XY(100, 0)));
                    st.Push(Fix.Free(Fix.XY(100, 100)));
                    st.Push(Fix.Free(Fix.XY(0, 100)));
                    st.SetEdge(0, Fix.TraceOf(Fix.XY(30, 1)));
                    st.SetEdge(1, Fix.TraceOf(Fix.XY(101, 40), Fix.XY(99, 70)));
                    st.SetEdge(2, Fix.TraceOf(Fix.XY(60, 101)));
                    var live = ProjectKit.Build(st, cfg, tuning);
                    AssertShape("P3 实时投影形状", st, cfg, tuning, new int[] { 1, 2, 1 });
                    Check.False("P3 闭合边（m3→m0）的点不在实时投影里", () =>
                    {
                        for (int i = 0; i < live.Slots.Count; i++)
                        {
                            if (live.Slots[i].OwnerEdge == 3) return true;
                            if (Harness.Near(live.Slots[i].Pos, Fix.XY(0, 50), 1e-9)) return true;
                        }
                        return false;
                    });
                    var closing = Fix.TraceOf(Fix.XY(0, 50), Fix.XY(-2, 20));
                    var ring = ProjectKit.BuildClosedRing(st, closing, cfg, tuning);
                    Check.Int("P3 闭合环 = 实时投影 8 格 + 闭合边 2 点", 10, ring.Count);
                    Check.True("P3 闭合环不重复首点（GameSide 自己决定要不要补闭合）", () =>
                        ring[ring.Count - 1].DistanceTo(ring[0]) > tuning.MinSpacing);
                    Check.True("P3 闭合环最后一格来自闭合边", () => Harness.Near(ring[ring.Count - 1], Fix.XY(-2, 20), 1e-9));
                    // 闭合边的尾巴正好落回首点附近 ⇒ 必须被裁掉，否则游戏三角化拿到重合点
                    var closingTouch = Fix.TraceOf(Fix.XY(0, 40), Fix.XY(0, 0.5));
                    var ring2 = ProjectKit.BuildClosedRing(st, closingTouch, cfg, tuning);
                    Check.True("P3 与首点重合的尾点被裁掉", () =>
                        ring2[ring2.Count - 1].DistanceTo(ring2[0]) > tuning.MinSpacing);
                    Check.Int("P3 裁掉重合点后剩 9 格", 9, ring2.Count);
                    var ringNull = ProjectKit.BuildClosedRing(st, null, cfg, tuning);
                    Check.Int("P3 没有闭合边结果 ⇒ 等价于实时投影", live.Slots.Count, ringNull.Count);
                });

                Harness.Section("ProjectKit 手动路径与续接路径（P4，反馈 4 术语 + 反馈 7 删弧）", () =>
                {
                    // 反馈 4 把两条路径定了名：**手动路径** = 首尾手动、中间没有任何自动点（直连）；
                    // **自动贴合路径** = 手动点之间那一串自动节点。反馈 7 又把「自由点之间也走弧线」
                    // 整条删掉，换成描边侧的「节点续接」。所以投影层这一页现在的规矩是两条：
                    //  ① 没有描边结果的边必须**一格都不插**（旧版在这里插 3 个弧点，那已经不是玩家画的那条边了）；
                    //  ② 续接路径（Continued=true）的点照样进投影，末尾那一格就是「中断处」的折点。
                    var tuning = Fix.Tun(AreaTier.Lot, 40, 0.5, 60, 2.0, 3.0);

                    // —— ① 自由边 = 直连
                    var st = Fix.StackWith(3, new int[] { 0, 5 });   // 边 0 无描边 ⇒ 手动路径；边 1 有描边 ⇒ 自动贴合路径
                    var p = ProjectKit.Build(st, ModConfig.CreateDefault(), tuning);
                    AssertShape("P4 手动路径不插点后的形状", st, ModConfig.CreateDefault(), tuning, new int[] { 0, 5 });
                    Check.Int("P4 3 手动 + 5 自动 = 8 格（自由边一个点都不插）", 8, p.Slots.Count);
                    Check.Int("P4 自动格全部来自那条有描边的边", 5, p.AutoCount);
                    Check.True("P4 手动路径上没有任何一格挂在边 0 上（弧化已随反馈 7 删除）", () =>
                    {
                        for (int i = 0; i < p.Slots.Count; i++)
                            if (p.Slots[i].OwnerEdge == 0) return false;
                        return true;
                    });
                    Check.True("P4 边 0 两端的手动点仍然相邻（中间没被塞东西 ⇒ 游戏看到的就是一条直边）", () =>
                    {
                        int m0 = -1, m1 = -1;
                        for (int i = 0; i < p.Slots.Count; i++)
                        {
                            if (!p.Slots[i].IsManual) continue;
                            if (m0 < 0) { m0 = i; continue; }
                            if (m1 < 0) { m1 = i; break; }
                        }
                        return m1 == m0 + 1;
                    });

                    // —— ② 续接路径：中间沿网络的那一段照贴，末尾留一个折点接回不在网络上的那一端
                    var st2 = new ManualStack();
                    st2.Push(Fix.Free(Fix.XY(0, 0)));
                    st2.Push(Fix.Free(Fix.XY(100, 0)));
                    var cont = Fix.TraceOf(Fix.XY(20, 8), Fix.XY(55, 8));
                    cont.Continued = true;
                    st2.SetEdge(0, cont);
                    var p2 = ProjectKit.Build(st2, ModConfig.CreateDefault(), tuning);
                    AssertShape("P4 续接边不破坏形状", st2, ModConfig.CreateDefault(), tuning, new int[] { 2 });
                    Check.True("P4 续接的两个中间点都进了投影，顺序不变", () =>
                        Harness.Near(p2.Slots[1].Pos, Fix.XY(20, 8), 1e-12) && Harness.Near(p2.Slots[2].Pos, Fix.XY(55, 8), 1e-12));
                    Check.True("P4 最后一格仍是手动节点（游戏的最小间距判定靠这条）", () =>
                        p2.Slots[p2.Slots.Count - 1].IsManual);

                    // —— ③ 节点上限：反馈 7 要「无限」，代码里只留一道存档侧硬保险
                    var many = Fix.StackWith(2, new int[] { 500 });
                    var pMany = ProjectKit.Build(many, ModConfig.CreateDefault(), Fix.Tun(AreaTier.Lot, 40, 0.5,
                        ModConfig.HARD_NODE_CAP, 2.0, 3.0));
                    Check.Int("P4 一条边 500 个自动点全部原样上屏（玩家侧不再有节点上限这项设置）", 502, pMany.Slots.Count);
                    Check.False("P4 未超硬保险 ⇒ 不该报 Truncated", () => pMany.Truncated);
                });

                Harness.Section("ManualStack 右键语义 = 弹掉一个手动节点（需求 3，R）", () =>
                {
                    var cfg = ModConfig.CreateDefault();
                    var tuning = Fix.Tun(AreaTier.District, 60, 0.5, 60, 2.0, 3.0);

                    // 三条边各自的描边点放在三条不同的水平线上，右键弹掉一条边就能用 y 坐标精确认出残留
                    var st = new ManualStack();
                    st.Push(Fix.Free(Fix.XY(0, 0)));
                    st.Push(Fix.Free(Fix.XY(100, 0)));
                    st.Push(Fix.Free(Fix.XY(200, 0)));
                    st.Push(Fix.Free(Fix.XY(300, 0)));
                    st.SetEdge(0, Fix.TraceOf(Fix.XY(40, 10), Fix.XY(70, 10)));
                    st.SetEdge(1, Fix.TraceOf(Fix.XY(140, 20), Fix.XY(170, 20)));
                    st.SetEdge(2, Fix.TraceOf(Fix.XY(210, 30), Fix.XY(220, 30), Fix.XY(230, 30), Fix.XY(240, 30), Fix.XY(250, 30), Fix.XY(260, 30), Fix.XY(290, 30)));
                    Check.Int("R 初始 4 个手动节点", 4, st.Count);
                    Check.Int("R 初始 3 条边", 3, st.Edges.Count);

                    var before = ProjectKit.Build(st, cfg, tuning);
                    Check.Int("R 弹前总槽位数 = 4 手动 + 11 自动", 15, before.Slots.Count);

                    Check.True("R 右键：真的弹掉了一个", () => st.PopLast());
                    Check.Int("R 弹后手动节点 4→3（恰好一个）", 3, st.Count);
                    Check.Int("R 弹后边数 3→2（恰好一条）", 2, st.Edges.Count);
                    Check.True("R 弹掉的是最后一个手动节点，前面的原封不动", () =>
                        Harness.Near(st.Nodes[0].Pos, Fix.XY(0, 0), 1e-12) &&
                        Harness.Near(st.Nodes[1].Pos, Fix.XY(100, 0), 1e-12) &&
                        Harness.Near(st.Nodes[2].Pos, Fix.XY(200, 0), 1e-12));
                    var after = ProjectKit.Build(st, cfg, tuning);
                    AssertShape("R 弹后形状仍成立", st, cfg, tuning, new int[] { 2, 2 });
                    Check.False("R 被弹掉那条边的描边点一个不剩（整批跟着消失）", () => HasY(after, 30));
                    Check.True("R 其余两边的描边点还在", () => HasY(after, 10) && HasY(after, 20));
                    Check.Int("R 弹后槽位数 15→7（少了 1 个手动 + 它那 7 个自动点）", 7, after.Slots.Count);

                    Check.True("R 第二次右键", () => st.PopLast());
                    Check.Int("R 手动节点 3→2", 2, st.Count);
                    Check.Int("R 边 2→1", 1, st.Edges.Count);
                    var after2 = ProjectKit.Build(st, cfg, tuning);
                    Check.False("R 第二条边的描边点也没了", () => HasY(after2, 20));
                    Check.True("R 只剩第一条边的描边点", () => HasY(after2, 10));

                    Check.True("R 第三次右键 ⇒ 只剩 1 个手动节点、0 条边", () => st.PopLast());
                    Check.Int("R 手动节点 2→1", 1, st.Count);
                    Check.Int("R 边 1→0", 0, st.Edges.Count);
                    var after3 = ProjectKit.Build(st, cfg, tuning);
                    Check.Int("R 单节点投影 = 1 格", 1, after3.Slots.Count);
                    Check.Int("R 单节点 ManualCount=1", 1, after3.ManualCount);
                    Check.Int("R 单节点无自动点", 0, after3.AutoCount);

                    // 「弹掉最后一个手动节点」= 玩家把整条区域放弃重画：不能抛，必须回到空栈
                    Check.Guard("R 弹掉唯一剩下的手动节点不抛异常", () =>
                    {
                        bool popped = st.PopLast();
                        Check.True("R 弹掉唯一节点返回 true", () => popped);
                        Check.Int("R 弹完剩 0 个手动节点", 0, st.Count);
                        Check.Int("R 弹完剩 0 条边", 0, st.Edges.Count);
                    });
                    var emptyProj = ProjectKit.Build(st, cfg, tuning);
                    Check.Int("R 空栈投影为空", 0, emptyProj.Slots.Count);
                    Check.Int("R 空栈 ManualCount=0", 0, emptyProj.ManualCount);
                    Check.False("R 空栈再右键返回 false 且不抛", () => st.PopLast());

                    // 空栈 Push 的顺序：1 个节点没有边，2 个节点才有第 0 条边
                    var s2 = new ManualStack();
                    Check.Int("R 空栈边数 0", 0, s2.Edges.Count);
                    s2.Push(Fix.Free(Fix.XY(0, 0)));
                    Check.Int("R 1 个节点 ⇒ 0 条边", 0, s2.Edges.Count);
                    s2.Push(Fix.Free(Fix.XY(10, 0)));
                    Check.Int("R 2 个节点 ⇒ 1 条边", 1, s2.Edges.Count);
                    s2.Push(Fix.Free(Fix.XY(20, 0)));
                    Check.Int("R 3 个节点 ⇒ 2 条边（边数恒 = 节点数-1）", 2, s2.Edges.Count);
                    Check.True("R 边数恒 = 节点数-1（Push/Pop 交替 20 次不变式）", () =>
                    {
                        var s = new ManualStack();
                        for (int i = 0; i < 20; i++)
                        {
                            s.Push(Fix.Free(Fix.XY(i * 10.0, 0)));
                            if (s.Edges.Count != Math.Max(0, s.Nodes.Count - 1)) return false;
                        }
                        for (int i = 0; i < 15; i++)
                        {
                            if (!s.PopLast()) return false;
                            if (s.Edges.Count != Math.Max(0, s.Nodes.Count - 1)) return false;
                        }
                        return s.Count == 5;
                    });
                    Check.True("R 弹栈后签名一定改变（游戏侧靠这个决定要不要重算）", () =>
                    {
                        var s = Fix.StackWith(3, new int[] { 2, 2 });
                        long a = s.Signature;
                        s.PopLast();
                        return a != s.Signature;
                    });
                    Check.True("R 弹栈后重建不残留被弹边的引用（SetEdge 越界静默忽略）", () =>
                    {
                        var s = Fix.StackWith(3, new int[] { 2, 2 });
                        s.PopLast();
                        s.SetEdge(9, Fix.TraceOf(Fix.XY(1, 1)));   // 越界：不该写进去、更不该抛
                        return s.Edges.Count == 1 && s.Edges[0] != null;
                    });
                });
            }

            // ———— 形状校验器：核心不变量只写一遍，所有用例共用 ————

            /// <summary>
            /// 逐格核对 [m0, T(m0→m1), m1, …, mN] 这个形状。
            /// expectedPerEdge 为 null 时按「min(该边描边点数, MaxNodesPerEdge)」自己算期望值。
            /// </summary>
            private static void AssertShape(string label, ManualStack stack, ModConfig cfg, ResolvedTuning tuning, int[] expectedPerEdge)
            {
                Projection p = ProjectKit.Build(stack, cfg, tuning);
                int n = stack.Count;
                Check.Int(label + "：ManualCount == 手动栈长度", n, p.ManualCount);
                Check.Int(label + "：槽位数 == 手动 + 自动", p.ManualCount + p.AutoCount, p.Slots.Count);

                if (n == 0)
                {
                    Check.True(label + "：空栈 ⇒ 零槽位", () => p.Slots.Count == 0);
                    return;
                }

                // ① 最后一格必须是手动节点
                Check.True(label + "：末格是手动节点（自动点绝不排在最后手动节点之后）", () =>
                    p.Slots[p.Slots.Count - 1].IsManual && LastManualIndex(p) == p.Slots.Count - 1);
                // ② 手动格的下标序列必须严格递增且逐个对上栈内容
                Check.True(label + "：手动格依次等于栈里的节点位置", () =>
                {
                    int mi = 0;
                    for (int i = 0; i < p.Slots.Count; i++)
                    {
                        if (!p.Slots[i].IsManual) continue;
                        if (mi >= n) return false;
                        if (!Harness.Near(p.Slots[i].Pos, stack.Nodes[mi].Pos, 1e-12)) return false;
                        if (p.Slots[i].OwnerEdge != -1) return false;
                        mi++;
                    }
                    return mi == n;
                });
                // ③ 每个自动格的 OwnerEdge 必须落在「它前面最近的那个手动节点」所对应的那条边
                Check.True(label + "：每个自动点归属于前一个手动节点之前的那条边", () =>
                {
                    int manualIdx = 0;          // 下一个要放的手动节点下标
                    for (int i = 0; i < p.Slots.Count; i++)
                    {
                        Slot s = p.Slots[i];
                        if (s.IsManual) { manualIdx++; continue; }
                        int wantEdge = manualIdx - 1;   // 这一批自动点属于 m(manualIdx-1) → m(manualIdx)
                        if (wantEdge < 0) return false;
                        if (s.OwnerEdge != wantEdge) return false;
                    }
                    return true;
                });
                // ④ 每边自动点数 = min(描边点数, 预算)
                Check.True(label + "：各边自动点数符合预算", () =>
                {
                    int[] got = new int[Math.Max(1, n - 1)];
                    for (int i = 0; i < p.Slots.Count; i++) if (!p.Slots[i].IsManual) got[p.Slots[i].OwnerEdge]++;
                    for (int e = 0; e + 1 < n; e++)
                    {
                        int want = expectedPerEdge != null && e < expectedPerEdge.Length
                            ? expectedPerEdge[e]
                            : ExpectedAutoCount(stack, e, cfg, tuning);
                        if (got[e] != want) return false;
                    }
                    return true;
                });
                // ⑤ 描边点内容逐格一致（顺序也是描边顺序，反了就说明区域边界会打结）
                Check.True(label + "：自动点内容与顺序 = 该边 TraceResult.Points 的前缀", () =>
                {
                    var buckets = new List<List<P3>>(Math.Max(1, n - 1));
                    for (int e = 0; e + 1 < n; e++) buckets.Add(new List<P3>());
                    for (int i = 0; i < p.Slots.Count; i++)
                    {
                        Slot s = p.Slots[i];
                        if (!s.IsManual) buckets[s.OwnerEdge].Add(s.Pos);
                    }
                    for (int e = 0; e + 1 < n; e++)
                    {
                        List<P3> src = TracePoints(stack, e, cfg, tuning);
                        if (src.Count != buckets[e].Count) return false;
                        for (int k = 0; k < src.Count; k++) if (!Harness.Near(src[k], buckets[e][k], 1e-12)) return false;
                    }
                    return true;
                });
            }

            private static List<P3> TracePoints(ManualStack stack, int e, ModConfig cfg, ResolvedTuning tuning)
            {
                TraceResult tr = (stack.Edges != null && e < stack.Edges.Count) ? stack.Edges[e] : null;
                if (tr != null && tr.Points != null && tr.Points.Count > 0)
                {
                    int take = Math.Min(tr.Points.Count, Math.Max(0, tuning.MaxNodesPerEdge));
                    return tr.Points.GetRange(0, take);
                }
                // 反馈 7：「自由放置的两点之间也走弧线」整条删除，换成描边侧的「节点续接」。
                // 于是投影层的规矩变简单了：**没有描边结果的边就是直的**（反馈 4 定义的「手动路径」），
                // 任何情况下都不许凭空往中间插点。（CurveKit.Arc 在产品代码里已无调用者 ——
                // 它和 v0.1.4 删掉海岸线后留下的 IsoKit 一样，暂时作为「已测纯几何」留在 Engine/ 里，
                // README 已知限制第 8 条记着这笔。）
                return new List<P3>();
            }

            private static int ExpectedAutoCount(ManualStack stack, int e, ModConfig cfg, ResolvedTuning tuning)
            {
                return TracePoints(stack, e, cfg, tuning).Count;
            }

            private static int LastManualIndex(Projection p)
            {
                for (int i = p.Slots.Count - 1; i >= 0; i--) if (p.Slots[i].IsManual) return i;
                return -1;
            }

            private static bool SameSlots(Projection a, Projection b)
            {
                if (a.Slots.Count != b.Slots.Count) return false;
                for (int i = 0; i < a.Slots.Count; i++)
                {
                    if (a.Slots[i].IsManual != b.Slots[i].IsManual) return false;
                    if (a.Slots[i].OwnerEdge != b.Slots[i].OwnerEdge) return false;
                    if (!Harness.Near(a.Slots[i].Pos, b.Slots[i].Pos, 1e-15)) return false;
                }
                return a.ManualCount == b.ManualCount && a.AutoCount == b.AutoCount && a.Truncated == b.Truncated;
            }

            private static bool HasY(Projection p, double y)
            {
                for (int i = 0; i < p.Slots.Count; i++) if (Math.Abs(p.Slots[i].Pos.Y - y) < 1e-9) return true;
                return false;
            }
        }
    }
}
