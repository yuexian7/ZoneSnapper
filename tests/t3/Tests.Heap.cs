using System;
using System.Collections.Generic;
using ZoneSnapper.Engine;

namespace ZoneSnapper.T3
{
    internal static partial class Tests
    {
        /// <summary>
        /// MinHeap（TraceKit 的 Dijkstra 底座）。同代价允许重复入堆是它存在的理由
        /// （SortedSet 会在同代价不同节点时互相顶掉），所以这里专门测重复代价。
        /// </summary>
        internal static class Heap
        {
            public static void Run()
            {
                Harness.Section("MinHeap 堆序（H）", () =>
                {
                    var h = new MinHeap(8);
                    Check.Int("H 初始为空", 0, h.Count);
                    int[] keys = { 5, 1, 9, 1, 3, 7, 3, 2, 8, 0, 4, 6, 1, 3, 9 };
                    for (int i = 0; i < keys.Length; i++) h.Push(keys[i], keys[i]);
                    Check.Int("H 计数正确（含重复代价）", keys.Length, h.Count);
                    double prev = double.NegativeInfinity;
                    bool sorted = true;
                    int popped = 0;
                    while (h.Count > 0)
                    {
                        int node; double prio;
                        h.Pop(out node, out prio);
                        popped++;
                        if (prio < prev - 1e-12) sorted = false;
                        prev = prio;
                    }
                    Check.True("H 弹出序列按代价不减", () => sorted);
                    Check.Int("H 全部弹出且不多不少", keys.Length, popped);
                    Check.False("H 弹空后 Count=0", () => h.Count != 0);
                    int n2 = -5; double p2 = -5;
                    Check.False("H 空堆 Pop 返回 false 而不是抛", () => h.Pop(out n2, out p2));
                    Check.Int("H 空堆 Pop 给出哨兵", -1, n2);

                    // 扩容：初值 8 远小于 500，Grow 路径必须不丢元素
                    var big = new MinHeap(4);
                    var want = new List<double>();
                    var rnd = new Random(20260918);     // 固定种子：这条断言失败时必须能原样复现
                    for (int i = 0; i < 500; i++)
                    {
                        double v = Math.Round(rnd.NextDouble() * 20);  // 故意大量同值，压重复代价路径
                        want.Add(v);
                        big.Push(i, v);
                    }
                    want.Sort();
                    var got = new List<double>();
                    while (big.Count > 0)
                    {
                        int nn; double pp;
                        big.Pop(out nn, out pp);
                        got.Add(pp);
                    }
                    bool same = got.Count == want.Count;
                    for (int i = 0; same && i < got.Count; i++) if (got[i] != want[i]) same = false;
                    Check.True("H 500 项（含大量同代价）扩容后弹出序列与排序结果逐项一致", () => same);
                    Check.True("H Pop 带回的是入堆时的节点号", () =>
                    {
                        var hh = new MinHeap(8);
                        hh.Push(111, 5);
                        hh.Push(222, 1);
                        int node; double prio;
                        hh.Pop(out node, out prio);
                        return node == 222 && prio == 1;
                    });
                    Check.True("H 负代价（起点接入代价可为 0/负数）照样按序", () =>
                    {
                        var hh = new MinHeap(8);
                        hh.Push(1, -5); hh.Push(2, -1); hh.Push(3, -9);
                        int node; double prio;
                        hh.Pop(out node, out prio);
                        return node == 3 && prio == -9;
                    });
                });
            }
        }
    }
}
