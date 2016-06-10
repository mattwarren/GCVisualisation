using System;
using System.Threading;

namespace SampleApp
{
    class Program
    {
        private static byte[] staticArray;
        static void Main(string[] args)
        {
            var random = new Random(12345);
            for (int i = 0; i < 10000; i++)
            {
                Console.WriteLine("Iteration {0,10:N0}", i);
                var temp = new byte[10000];
                if (i % 10 == 0)
                    staticArray = temp;
                Thread.Sleep(random.Next(5, 20));
                //Thread.Sleep(random.Next(50, 500));
            }
            Console.WriteLine("Sample App has completed");
        }
    }
}
