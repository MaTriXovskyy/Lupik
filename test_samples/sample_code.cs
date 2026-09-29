// QuickPeek Test Sample: C# Source Code
using System;
using System.Collections.Generic;
using System.Linq;

namespace QuickPeek.Demo
{
    public class PerformanceBenchmark
    {
        public string Title { get; set; } = "QuickPeek Performance Test";
        public double LatencyMilliseconds { get; set; } = 12.4;

        public static void Main(string[] args)
        {
            Console.WriteLine("QuickPeek działa błyskawicznie!");
            var benchmark = new PerformanceBenchmark();
            Console.WriteLine($"Średni czas reakcji na Spację: {benchmark.LatencyMilliseconds} ms");
        }
    }
}
