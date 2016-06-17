using System;
using System.Threading;
using System.Collections.Concurrent;
using System.Linq;

namespace SampleApp
{
    class Program
    {
        private static byte[] staticArray;
        static void Main(string[] args)
        {
            GCBenchmark();
            Console.WriteLine("Sample App has completed");
            return;

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

        private static void GCBenchmark()
        {
            // From http://prl.ccs.neu.edu/blog/2016/05/24/measuring-gc-latencies-in-haskell-ocaml-racket/
            // also see https://blog.pusher.com/latency-working-set-ghc-gc-pick-two/
            // This is the Haskell code we want to replicate in C#
            // Is uses an immutable associative Map data structure
            // So we'll use the .NET ConcurrentDictionary instead

            // type Msg = ByteString.ByteString
            // type Chan = Map.Map Int Msg

            // windowSize = 200000
            // msgCount = 1000000

            // message::Int->Msg
            // message n = ByteString.replicate 1024(fromIntegral n)

            // pushMsg::Chan->Int->IO Chan
            // pushMsg chan highId =
            // Exception.evaluate $
            //     let lowId = highId - windowSize in
            //     let inserted = Map.insert highId(message highId) chan in
            //     if lowId < 0 then inserted
            //     else Map.delete lowId inserted

            // main ::IO()
            // main = Monad.foldM_ pushMsg Map.empty[0..msgCount]

            var windowSize = 200000;
            var msgCount = 1000000;
            var map = new ConcurrentDictionary<int, byte[]>(); // could we pre-size?
            //var map = new ConcurrentDictionary<int, byte[]>(2, capacity: windowSize);

            foreach (var highId in Enumerable.Range(0, msgCount))
            {
                var lowId = highId - windowSize;
                var msg = new byte[1024];
                // replicate n x is a ByteString of length n with x the value of every element.
                byte data = (byte)(highId % 256);
                //for (int i = 0; i < msg.Length; i++)
                //    msg[i] = data;
                MemSet(msg, data);

                var inserted = map.AddOrUpdate(highId, msg, (key, value) => msg);
                if (lowId >= 0)
                {
                    byte[] removed;
                    map.TryRemove(lowId, out removed);
                }
            }

            Console.WriteLine("Concurrent Dictionary contains {0:N0} items", map.Count);
            Thread.Sleep(2500); // So we can see the msg, before the Console closes
        }

        public static void MemSet(byte[] array, byte value)
        {
            if (array == null)
            {
                throw new ArgumentNullException("array");
            }

            int block = 32, index = 0;
            int length = Math.Min(block, array.Length);

            //Fill the initial array
            while (index < length)
            {
                array[index++] = value;
            }

            length = array.Length;
            while (index < length)
            {
                Buffer.BlockCopy(array, 0, array, index, Math.Min(block, length - index));
                index += block;
                block *= 2;
            }
        }
    }
}
