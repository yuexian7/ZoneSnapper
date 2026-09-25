using System;
using System.Collections.Generic;

namespace ZoneSnapper.Engine
{
    /// <summary>
    /// 「已经存在的区域」怎么跟着路网走（需求 5 + 本轮反馈的第 6 条）。
    ///
    /// 【为什么单独一层】落档之后的区域只剩一圈坐标 —— 绘制时那些「这一格吸在哪条边的哪一侧、
    /// 弧长多少」的锚点信息并没有存进存档（游戏也没有地方存）。所以要跟着路网走，就必须
    /// **从位置反推锚点**，再用同一套描边把边重画一遍。这件事绘制中（拖拽改形）和提交后
    /// （自动跟随）是同一段逻辑，只写一遍放在这里，两边共用：
    /// 两套实现必然漂移，而漂移的表现是「画的时候贴合，改一下就全乱了」——最难查的那种。
    ///
    /// 【两条硬规矩，都是「半死不活比完全不工作有害得多」的具体形式】
    ///  ① <b>不许拖走玩家的几何</b>：反推出的锚点离原位置超过贴合半径 ⇒ 这一格按自由点处理，
    ///     位置一个毫米都不动。跟随的目的是让边界贴回** Moved 的那条路**，不是把整块地皮
    ///     搬到另一条路上去。
    ///  ② <b>自检不过就整圈不写</b>：重描出来的环自交或退化 ⇒ <see cref="FollowResult.Rejected"/>，
    ///     调用方保持原样。写坏一个已存档的区域比不跟随严重得多（三角化失败会让区域隐形，
    ///     连带建筑不归属，见 ZoneSnapperApplySystem 的同类护栏）。
    /// </summary>
    public static class FollowKit
    {
        /// <summary>整圈重描的结果。</summary>
        public sealed class FollowResult
        {
            /// <summary>每一格反推出的锚点（与输入 ring 一一对应；被规则①拦下的那格是 Free 锚点）。</summary>
            public readonly List<PlacedNode> Anchors = new List<PlacedNode>();

            /// <summary>展开后的交付环：锚点 + 各边描边点，不重复首点。约定与 ProjectKit.BuildClosedRing 一致。</summary>
            public readonly List<P3> Ring = new List<P3>();

            /// <summary>真正沿网络/边界走成功的边数与退回直线的边数（日志与实机判读用这两个数）。</summary>
            public int Traced;
            public int Straight;

            /// <summary>有几格被重新吸附挪了位置（=0 说明这一圈本来就在路上，不用写）。</summary>
            public int Moved;

            /// <summary>
            /// 这一圈里拆掉了几个**尖刺接缝**（左右两条走线在同一个位置接进网络，见 <see cref="Despike"/>）。
            /// 引擎层没有日志器，所以把数带出去给 GameSide 的统计行 —— 实机看它是不是在涨，
            /// 就知道「玩家那格不被吸附」与「走线自己长出接入点」这两条规则有没有在他眼前打架。
            /// </summary>
            public int Despiked;

            /// <summary>整圈是否被自检拦下（拦下时 <see cref="Ring"/> 不可用）。</summary>
            public bool Rejected;
            public TraceFallback RejectReason = TraceFallback.None;

            /// <summary>逐条边的回退原因（Edges[i] = 第 i 格 → 第 i+1 格，闭合时最后一条回到第 0 格）。</summary>
            public readonly List<TraceFallback> EdgeFallbacks = new List<TraceFallback>();
        }

        /// <summary>拖拽改形的结果：只关心被拖那一格与左右邻居之间的两段。</summary>
        public sealed class DragResult
        {
            public PlacedNode Prev;
            public PlacedNode Dragged;
            public PlacedNode Next;
            public int PrevIndex = -1;
            public int NextIndex = -1;

            /// <summary>prev → dragged 之间的描边点（顺序就是从前邻居走向被拖格）。</summary>
            public readonly List<P3> Before = new List<P3>();

            /// <summary>dragged → next 之间的描边点。</summary>
            public readonly List<P3> After = new List<P3>();

            public TraceFallback BeforeFallback = TraceFallback.None;
            public TraceFallback AfterFallback = TraceFallback.None;
            public bool Valid;

            /// <summary>左右两段本来会在同一个位置各接进网络一次（零宽度裂缝）⇒ 拆掉了一个（见 Around 里的尖刺守卫）。</summary>
            public bool Despiked;
        }

        /// <summary>
        /// 整圈重描。<paramref name="closed"/> = false 时用于「还没闭合的绘制中路径」：
        /// 少算最后一条回边，其余口径完全一致。
        /// <paramref name="minRing"/> 是允许的最少格数（闭合要 3，改形预览 2 就够）。
        /// </summary>
        public static FollowResult Rebuild(IList<P3> ring, bool closed, WorldSnapshot world, ModConfig cfg,
                                           ResolvedTuning tuning)
        {
            FollowResult res = new FollowResult();
            // ResolvedTuning 是 struct，没有 null 可判（判了也只是编译期好看）；
            // 真正要防的是「没跑过 PolicyKit.Resolve 的零值」——半径 0 时下面每一步都会自然退化成不贴合。
            if (ring == null || world == null || cfg == null) { res.Rejected = true; return res; }
            int n = ring.Count;
            if (n < (closed ? 3 : 2)) { res.Rejected = true; res.RejectReason = TraceFallback.NotOnNetwork; return res; }

            P3 centroid = Centroid(ring);

            // —— 1) 从位置反推锚点
            for (int i = 0; i < n; i++)
            {
                SnapKit.SnapExtra extra = new SnapKit.SnapExtra();
                extra.HasCentroid = true;
                extra.Centroid = centroid;
                if (i > 0) { extra.HasPrevious = true; extra.Previous = res.Anchors[i - 1]; }
                // 链条从第 0 格开始、没有前驱：跨越判据与侧向粘性都是局部启发，
                // 闭合环上「最后一格 → 第 0 格」那一条的上下文由描边阶段负责（那里看的是整圈）。
                res.Anchors.Add(AnchorAt(ring[i], world, cfg, tuning, extra, res));
            }

            // —— 2) 逐条边重描（结果要留着给第 3 步展开，图搜索一遍就够）
            int edges = closed ? n : n - 1;
            TraceContext ctx = new TraceContext();
            FillCtx(ring, ref ctx);
            List<TraceResult> trs = new List<TraceResult>(edges);
            for (int i = 0; i < edges; i++)
            {
                int j = i + 1 == n ? 0 : i + 1;
                PlacedNode a = res.Anchors[i];
                PlacedNode b = res.Anchors[j];
                ctx.HasRingEdge = true;
                ctx.RingEdge = i;              // 本条走线替换的就是环上第 i 格：自交判定要按这个口径拼
                TraceResult tr = TraceKit.Trace(a, b, world, cfg, tuning, ctx);
                bool along = tr.UsedNetwork || tr.UsedBorder;
                if (along) res.Traced++; else res.Straight++;
                res.EdgeFallbacks.Add(tr.Fallback);
                trs.Add(tr);
            }

            // —— 2c) 尖刺守卫（第八轮反馈 3/10 与 6/8 撞车的正是这一处）
            //   「玩家那一格不许被挪动」与「走线要自己长出接入点」合起来会有一个退化形状：
            //   玩家把一格从路缘上拽开（或天生就落在路缘正旁边几米），它**左右两条走线各自**
            //   在同一个位置接进网络 ⇒ 环变成 …X → P → X… ，同一个坐标被访问两次（零宽度的裂缝）。
            //   这种环游戏是能存下来的，但三角化出的就是一块「看不见的区域」，
            //   正是反馈 4「路径不显示 / 放置与预览不一致」那一类症状里最阴的一种。
            //   处理：两处重合的接入点只留**一个**，留哪边按「哪一侧的走线更沿网络铺」——
            //   点数多的那侧保留（它承载了真正的贴合），另一侧退回玩家自己那笔直线。
            //   同点数时保留左侧：纯粹为了确定性，同一份输入永远得到同一个环（跟随每帧重算靠这个）。
            Despike(res.Anchors, trs, closed, edges, Math.Max(tuning.MinSpacing, 0.5), res);

            // —— 3) 展开成交付环（只在闭合时用；不闭合时调用方自己按绘制中的形状投影）
            for (int i = 0; i < n; i++) res.Ring.Add(res.Anchors[i].Pos);
            if (closed)
            {
                for (int i = 0; i < trs.Count; i++)
                {
                    TraceResult tr = trs[i];
                    if (tr.Points != null) for (int k = 0; k < tr.Points.Count; k++) res.Ring.Add(tr.Points[k]);
                }
                // 描边点在「简化之前」已经各自守过间距，这里只统一处理与首点重合的尾巴。
                while (res.Ring.Count > 1 && res.Ring[res.Ring.Count - 1].DistanceTo(res.Ring[0]) <= tuning.MinSpacing)
                {
                    res.Ring.RemoveAt(res.Ring.Count - 1);
                }
            }

            // —— 4) 自检
            if (closed)
            {
                List<P3> full = Close(res.Ring);
                if (SimplifyKit.SelfIntersects(full))
                {
                    res.Rejected = true;
                    res.RejectReason = TraceFallback.WouldSelfIntersect;
                    res.Ring.Clear();
                    return res;
                }
                double area = Math.Abs(GeoKit.SignedArea2D(res.Ring));
                if (area < 25.0)
                {
                    res.Rejected = true;
                    res.RejectReason = TraceFallback.WouldDegenerate;
                    res.Ring.Clear();
                    return res;
                }
            }
            return res;
        }

        /// <summary>
        /// 拖拽改形（需求 3 的第二种模式）：玩家在已闭合的区域上抓住一格拖动时，要看的是
        /// **和它左右相邻的那两格**，不是「先后点的两格」—— 所以这里只重描相邻两段，
        /// 其余每一格、每一条边都原样不动（动了就是模组在玩家没碰的地方改他的存档）。
        /// </summary>
        public static DragResult Around(IList<P3> ring, bool closed, int nodeIndex, WorldSnapshot world,
                                        ModConfig cfg, ResolvedTuning tuning)
        {
            DragResult d = new DragResult();
            if (ring == null || world == null || cfg == null) return d;
            int n = ring.Count;
            if (n < (closed ? 3 : 2)) return d;
            if (nodeIndex < 0 || nodeIndex >= n) return d;

            P3 centroid = Centroid(ring);
            SnapKit.SnapExtra self = new SnapKit.SnapExtra();
            self.HasCentroid = true;
            self.Centroid = centroid;
            // 第八轮反馈 3/10：**拖到哪一格，那一格就停在哪**。
            // 重锚照做（要拿它问「这一格现在贴在哪条路上」，描边要用那个方向），但坐标必须还回玩家松手的位置：
            // 老写法把 d.Dragged.Pos 直接写进交付环（LocalChain 第 232 行），等于
            // 「玩家松手之后模组又把他刚放的那一格挪了几米」——正是这一轮被点名两次的症状。
            // 邻居两格同理（下面 Prev/Next 也换成 KeepPos）：LocalChain 写的是 ring[PrevIndex]/ring[NextIndex]
            // 原坐标，而描边拿的却是重锚后的坐标 ⇒ 落档几何与预览几何差着一截（反馈 4「放置路径和预览路径不一致」）。
            // 端点坐标一致之后，那两段走线的起止点与最终写进环里的点才是同一个点。
            d.Dragged = AnchorKeepPos(ring[nodeIndex], world, cfg, tuning, self, null);

            int pi = closed ? (nodeIndex + n - 1) % n : nodeIndex - 1;
            int ni = closed ? (nodeIndex + 1) % n : nodeIndex + 1;
            d.PrevIndex = (pi >= 0 && pi < n) ? pi : -1;
            d.NextIndex = (ni >= 0 && ni < n) ? ni : -1;
            TraceContext ctx = new TraceContext();
            FillCtx(ring, ref ctx);

            if (d.PrevIndex >= 0)
            {
                SnapKit.SnapExtra e = new SnapKit.SnapExtra();
                e.HasCentroid = true;
                e.Centroid = centroid;
                e.HasPrevious = true;
                e.Previous = d.Dragged;         // 跨越判据要看「邻居 → 被拖格」这条直线
                d.Prev = AnchorKeepPos(ring[d.PrevIndex], world, cfg, tuning, e, null);
                ctx.HasRingEdge = true;
                ctx.RingEdge = d.PrevIndex;  // 被替换的是「左邻居 → 被拖格」这一格
                TraceResult tr = TraceKit.Trace(d.Prev, d.Dragged, world, cfg, tuning, ctx);
                d.BeforeFallback = tr.Fallback;
                AddAll(d.Before, tr.Points);
            }
            if (d.NextIndex >= 0)
            {
                SnapKit.SnapExtra e = new SnapKit.SnapExtra();
                e.HasCentroid = true;
                e.Centroid = centroid;
                e.HasPrevious = true;
                e.Previous = d.Dragged;
                d.Next = AnchorKeepPos(ring[d.NextIndex], world, cfg, tuning, e, null);
                ctx.HasRingEdge = true;
                ctx.RingEdge = nodeIndex;    // 被替换的是「被拖格 → 右邻居」这一格
                TraceResult tr = TraceKit.Trace(d.Dragged, d.Next, world, cfg, tuning, ctx);
                d.AfterFallback = tr.Fallback;
                AddAll(d.After, tr.Points);
            }
            // 尖刺守卫（与整圈重描同一条口径，见 Despike 的说明）：被拖这一格左右两条走线
            // 在同一个位置接进网络 ⇒ 那是零宽度的裂缝，只留贴合内容多的那一侧。
            if (d.Before.Count > 0 && d.After.Count > 0
                && d.Before[d.Before.Count - 1].DistanceTo(d.After[0]) <= Math.Max(tuning.MinSpacing, 0.5))
            {
                if (d.Before.Count >= d.After.Count) d.After.RemoveAt(0);
                else d.Before.RemoveAt(d.Before.Count - 1);
                d.Despiked = true;
            }

            // 「有效」的判据是「这一格的左右邻居认出来了」，**不是**「它自己吸到了网络上」——
            // 第八轮反馈 3/10 之后玩家那格本来就不该被吸附（Kind 通常就是 Free），
            // 老写法在这里会把一次正常的拖拽判成无效 ⇒ SpliceLocal 交回空表 ⇒ 松手后一个字节都不写，
            // 症状正是第六轮反馈 6 那句「放置后还是在原位」。
            d.Valid = d.PrevIndex >= 0 || d.NextIndex >= 0;
            return d;
        }

        /// <summary>
        /// 被替换掉的那一段**局部链**：[左邻] + 左边重描点 + [被拖格] + 右边重描点 + [右邻]。
        ///
        /// 为什么要单独有它：拖拽落档那一路的自检**只看这段链**，不看整环。整环自交闸在
        /// 「玩家自己画出来的密点环」上几乎每次都成立（游戏落档的环可以密到几十厘米一格，
        /// 数值上很容易擦出交点），拿它当闸的结果就是第六轮反馈 6：预览画得好好的，
        /// 松手后一个字节都没写。局部链自交才是真错误 —— 那说明重描的两段打了个结。
        /// </summary>
        public static List<P3> LocalChain(IList<P3> ring, DragResult d)
        {
            List<P3> chain = new List<P3>();
            if (ring == null || d == null || !d.Valid) return chain;
            int n = ring.Count;
            if (n < 3) return chain;
            if (d.PrevIndex < 0 || d.NextIndex < 0 || d.PrevIndex >= n || d.NextIndex >= n) return chain;
            chain.Add(ring[d.PrevIndex]);
            AddAll(chain, d.Before);
            chain.Add(d.Dragged.Pos);
            AddAll(chain, d.After);
            chain.Add(ring[d.NextIndex]);
            return chain;
        }

        /// <summary>
        /// 把 <see cref="Around"/> 的结果拼回整环：只有局部链被换掉，其余每一格原样保留。
        ///
        /// 两个刻意的取舍，都是玩家的话直接要求的：
        ///  · **左右邻居写回的是它们原来的坐标**（<c>ring[PrevIndex]</c>），不是 Around 重锚之后的
        ///    <c>d.Prev.Pos</c>。重锚只是为了给描边算「跨越判据」用的方向，写回去就成了
        ///    「模组把玩家没碰的格子挪了」（第六轮反馈 3：手动节点除城市边界外不许吸附）。
        ///  · 从左邻开始写，等于把闭合环整体旋转了几格。闭合环没有「起点」语义
        ///    （游戏按环形索引读），旋转无害；换来的是拼接口径只有一条，不必按 nodeIndex 分三种情况。
        ///
        /// 返回空表 = 输入不合法（环太短、下标越界、结果无效），调用方应保持原样，不要猜。
        /// </summary>
        public static List<P3> SpliceLocal(IList<P3> ring, DragResult d)
        {
            List<P3> nr = LocalChain(ring, d);
            if (nr.Count == 0) return nr;
            int n = ring.Count;
            for (int i = 1; i <= n - 3; i++) nr.Add(ring[(d.NextIndex + i) % n]);
            return nr;
        }

        /// <summary>
        /// 从位置反推一格锚点，并执行规则①「不许把玩家的几何拖走」。
        /// 落在自由点上也算正常结果（那条路可能已经被拆了 ⇒ 边界留在原地，比乱跳到别的路好）。
        /// </summary>
        private static PlacedNode AnchorAt(P3 pos, WorldSnapshot world, ModConfig cfg, ResolvedTuning tuning,
                                           SnapKit.SnapExtra extra, FollowResult res)
        {
            PlacedNode a = SnapKit.FindSnap(pos, world, cfg, tuning, SnapKit.FreeNode(pos), extra);
            if (a.Kind != SnapKind.Free && a.Pos.DistanceTo(pos) > tuning.SnapRadius)
            {
                a = SnapKit.FreeNode(pos);
            }
            if (res != null && a.Pos.DistanceTo(pos) > 1e-6) res.Moved++;
            return a;
        }

        /// <summary>
        /// 与 <see cref="AnchorAt"/> 同一个锚点，但**坐标还回玩家那一个**。
        /// 用途：凡是「这一格是玩家放的」的场合（拖拽改形的三格）都走这里 ——
        /// 描边需要知道它属于哪条边/哪一侧（Kind/Edge/Arc/Side 照用重锚结果），
        /// 而写进存档的坐标必须是玩家那一格。
        /// ⚠ 描边里切子段用的是 <c>ArcOnLine(a, line, side, total)</c>（按**位置**重算弧长），
        ///   不是直接用 a.Arc，所以换了坐标不会把子段切到边的另一头去（回归壳 X9f 钉这条）。
        /// </summary>
        private static PlacedNode AnchorKeepPos(P3 pos, WorldSnapshot world, ModConfig cfg, ResolvedTuning tuning,
                                                SnapKit.SnapExtra extra, FollowResult res)
        {
            PlacedNode a = AnchorAt(pos, world, cfg, tuning, extra, res);
            a.Pos = pos;
            return a;
        }

        /// <summary>
        /// 环上相邻两条走线的**接缝**是不是同一个位置（⇒ 那是一个零宽度尖刺，见
        /// <see cref="Despike"/> 的说明）。两侧都没有点时谈不上重合，返回 false。
        /// </summary>
        private static bool SameJoint(TraceResult tail, TraceResult head, double tol)
        {
            if (tail == null || head == null) return false;
            if (tail.Points == null || head.Points == null) return false;
            if (tail.Points.Count == 0 || head.Points.Count == 0) return false;
            return tail.Points[tail.Points.Count - 1].DistanceTo(head.Points[0]) <= tol;
        }

        /// <summary>
        /// 把重合的接缝拆掉：只留走线更沿网络铺的那一侧（点数多的），另一侧退回玩家自己那笔直线。
        /// 两侧点数相同 ⇒ 留左侧（纯粹为了确定性，见调用处注释）。
        /// </summary>
        private static void KeepOneJoint(TraceResult tail, TraceResult head)
        {
            if (tail.Points.Count >= head.Points.Count) head.Points.RemoveAt(0);
            else tail.Points.RemoveAt(tail.Points.Count - 1);
        }

        /// <summary>
        /// 整圈重描的尖刺守卫：逐个环上格子检查「左右两条走线在同一个位置接进网络」。
        /// 只在闭合环上处理内部接缝；开放（绘制中）路径同样逐格查，最后一格右侧本来没有走线。
        /// </summary>
        private static void Despike(IList<PlacedNode> anchors, List<TraceResult> trs, bool closed, int edges,
                                    double tol, FollowResult res)
        {
            if (anchors == null || trs == null) return;
            int n = anchors.Count;
            for (int i = 0; i < n; i++)
            {
                int prev = closed ? i - 1 + edges : i - 1;
                if (prev < 0 || prev >= trs.Count || i >= trs.Count) continue;
                if (!SameJoint(trs[prev], trs[i], tol)) continue;
                KeepOneJoint(trs[prev], trs[i]);
                if (res != null) res.Despiked++;
            }
        }

        /// <summary>从位置反推一格锚点（<see cref="AnchorAt"/> 的「坐标不动」版本在 AnchorKeepPos）。</summary>
        private static void AddAll(List<P3> dst, List<P3> src)
        {
            if (src == null) return;
            for (int i = 0; i < src.Count; i++) dst.Add(src[i]);
        }

        private static List<P3> Close(List<P3> ring)
        {
            List<P3> l = new List<P3>(ring.Count + 1);
            for (int i = 0; i < ring.Count; i++) l.Add(ring[i]);
            if (l.Count > 0) l.Add(l[0]);
            return l;
        }

        /// <summary>质心 + 已有环（和绘制中同一口径：描边的绕行方向、退化判定都要它们）。</summary>
        private static void FillCtx(IList<P3> ring, ref TraceContext ctx)
        {
            ctx.HasCentroid = true;
            ctx.Centroid = Centroid(ring);
            List<P3> l = new List<P3>(ring.Count);
            for (int i = 0; i < ring.Count; i++) l.Add(ring[i]);
            ctx.ExistingRing = l;
            ctx.Orientation = Math.Sign(GeoKit.SignedArea2D(l));
        }

        /// <summary>
        /// 多边形**面积**质心，而不是顶点平均：顶点平均在「一条长边上铺了一串密集描边点、
        /// 另一条边只有两个端点」的环上会明显偏向铺点那一侧 —— 而跟随场景的环正是这个形状
        /// （我们写进去的自动点本来就密）。侧向选择一旦被它带偏，边界就会翻到马路对面。
        /// 退化（面积≈0）时退回顶点平均。
        /// </summary>
        public static P3 Centroid(IList<P3> ring)
        {
            if (ring == null || ring.Count == 0) return new P3(0, 0, 0);
            double sx = 0, sy = 0, area = 0;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                P3 a = ring[i];
                P3 b = ring[(i + 1) % n];
                double cross = a.X * b.Y - b.X * a.Y;
                area += cross;
                sx += (a.X + b.X) * cross;
                sy += (a.Y + b.Y) * cross;
            }
            area *= 0.5;
            if (Math.Abs(area) < 1e-6)
            {
                double mx = 0, my = 0;
                for (int i = 0; i < n; i++) { mx += ring[i].X; my += ring[i].Y; }
                return new P3(mx / n, my / n, 0);
            }
            return new P3(sx / (6.0 * area), sy / (6.0 * area), 0);
        }
    }
}
