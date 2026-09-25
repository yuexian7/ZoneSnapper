using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ZoneSnapper.T3
{
    /// <summary>
    /// ZoneSnapper 的 T3 离线回归壳（设计文档 §六「不开游戏能测的全先测」）。
    ///
    /// 为什么值得为它写一整个工程：一次实机回归 = 约 95 秒启动 + 玩家手动画区域，
    /// 而这里全部断言跑完不到 1 秒。凡是能关在 Engine/ 纯函数层里的判定，都不该占用实机轮次。
    ///
    /// 硬约束（见 T3.csproj 的注释）：零游戏 DLL、零 <Reference>、零联网、零写盘、不碰部署目录。
    /// 退出码：0 = 全绿；1 = 有失败。verify.mjs 只需读退出码 + 最后一行的 PASS/FAIL 计数。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] argv)
        {
            TryUtf8Output();
            var sw = Stopwatch.StartNew();
            Console.WriteLine("ZoneSnapper T3 —— Engine/*.cs 纯函数回归（零游戏 DLL / 零联网 / 不开游戏）");
            Console.WriteLine("被测源码：" + System.IO.Path.GetFullPath(System.IO.Path.Combine("..", "..", "Engine")) + "（与模组 csproj 同一批文件）");

            Tests.Geo.Run();
            Tests.Simplify.Run();
            Tests.Project.Run();
            Tests.Trace.Run();
            Tests.Junctions.Run();
            Tests.Snap.Run();
            Tests.Targets.Run();
            Tests.Follow.Run();
            Tests.Rebind.Run();
            Tests.Preview.Run();
            Tests.Policy.Run();
            Tests.Curve.Run();
            Tests.Iso.Run();
            Tests.Store.Run();
            Tests.Heap.Run();
            Tests.Locale.Run();

            sw.Stop();
            Check.PrintFailures();
            Console.WriteLine();
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "总计 {0} 条：通过 {1}，失败 {2}，耗时 {3} ms", Check.Total, Check.Pass, Check.Fail, sw.ElapsedMilliseconds));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "PASS {0} / FAIL {1}", Check.Pass, Check.Fail));
            return Check.Fail == 0 ? 0 : 1;
        }

        private static void TryUtf8Output()
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); }
            catch { /* 输出被重定向时设不了编码，退回默认码页；数字与 ASCII 前缀不受影响 */ }
        }
    }
}
