using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Session;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GCVisualisation
{
    class Program
    {
        private static TraceEventSession session;
        private static ConcurrentBag<int> ProcessIdsUsedInRuns = new ConcurrentBag<int>();
        private static long totalBytesAllocated, gen0, gen1, gen2, gen2Background, gen3;

        static void Main(string[] args)
        {
            // TODO
            // - allow to specify PID of running process (DON'T kill it at the end!!)
            // - put some stats in a "margin" and display the output on the RH 80% of the screen
            // - GC pauses, for info see 
            //   - https://blogs.msdn.microsoft.com/maoni/2014/12/22/gc-etw-events-1/
            //   - https://blogs.msdn.microsoft.com/maoni/2014/12/25/gc-etw-events-3/
            //   - https://blogs.msdn.microsoft.com/maoni/2015/11/20/are-you-glad/
            //   - https://blogs.msdn.microsoft.com/maoni/2006/06/07/suspending-and-resuming-threads-for-gc/

            var sessionName = "GCVisualiser";
            session = new TraceEventSession(sessionName);
            session.EnableProvider(ClrTraceEventParser.ProviderGuid,
                                   TraceEventLevel.Verbose,
                                   (ulong)(ClrTraceEventParser.Keywords.GC));

            // The ETW collection thread starts receiving events immediately, but we only care about one process
            Task.Factory.StartNew(StartProcessingEvents, TaskCreationOptions.LongRunning);

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = args[0];
            var process = Process.Start(startInfo);
            ProcessIdsUsedInRuns.Add(process.Id);
            process.Exited += (sender, e) => Console.WriteLine("\nProcess has exited\n");
            Console.CancelKeyPress += (sender, e) =>
            {
                Console.WriteLine("\nConsole being killed, tidying up\n");
                session.Dispose();
            };

            PrintSymbolInformation();
            
            Console.WriteLine("Visualising GC Events, press <ENTER>, <ESC> or 'q' to exit");
            ConsoleKeyInfo cki;
            while (true)
            {
                cki = Console.ReadKey();
                if (cki.Key == ConsoleKey.Enter ||
                    cki.Key == ConsoleKey.Escape || 
                    cki.Key == ConsoleKey.Q)
                {
                    break;
                }

                if (cki.Key == ConsoleKey.S)
                {
                    lock (ConsoleLock)
                    {
                        PrintSummaryInfo();
                    }
                }
            }
            PrintSummaryInfo();
            Console.WriteLine();

            if (process.HasExited == false)
                process.Kill();
            session.Dispose();
        }

        private static void PrintSummaryInfo()
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            if (Console.CursorLeft > 0)
                Console.WriteLine();
            Console.WriteLine("Memory Allocations:");
            // 100GB = 107,374,182,400 bytes, so min-width = 15
            Console.WriteLine("  {0,15:N0} bytes currently allocated", GC.GetTotalMemory(forceFullCollection: false));
            Console.WriteLine("  {0,15:N0} bytes allocated in total", totalBytesAllocated);
            //Console.WriteLine("GC Collections - Gen0: {0:N0}, Gen1: {1:N0}, Gen2 {2:N0}, Gen2 B/G: {3:N0}, Gen3: {4:N0}",
            //                  gen0, gen1, gen2, gen2Background, gen3);
            Console.WriteLine("GC Collections:");
            Console.WriteLine("  {0,4:N0} - generation 0", gen0);
            Console.WriteLine("  {0,4:N0} - generation 1", gen1);
            Console.WriteLine("  {0,4:N0} - generation 2", gen2);
            Console.WriteLine("  {0,4:N0} - generation 2 B/G", gen2Background);
            if (gen3 > 0)
                Console.WriteLine("  {0,4:N0} - generation 3 (LOH)", gen3);

            Console.ResetColor();
        }

        private static void PrintSymbolInformation()
        {
            Console.WriteLine("Key to symbols:");
            Console.WriteLine(" - '.'     represents ~100K of memory ALLOCATIONS");

            Console.Write(" - ");
            Console.ForegroundColor = GetColourForGC(0); Console.Write("0");
            Console.ResetColor(); Console.Write("/");
            Console.ForegroundColor = GetColourForGC(1); Console.Write("1");
            Console.ResetColor(); Console.Write("/");
            Console.ForegroundColor = GetColourForGC(2); Console.Write("2");
            Console.ResetColor();
            Console.WriteLine("   indicates a FOREGROUND GC Collection, for the given generation (0, 1 or 2)");

            Console.Write(" - ");
            Console.BackgroundColor = GetColourForGC(2); Console.ForegroundColor = ConsoleColor.Black; Console.Write("2");
            Console.ResetColor();
            Console.WriteLine("       indicates a BACKGROUND GC Collection (Gen 2 only)");

            Console.WriteLine(" - ░/▒/▓/█ indicates a PAUSE due to a GC Collection");
            Console.WriteLine("   - ░ up to 25 msecs");
            Console.WriteLine("   - ▒ 25 to 50 msecs");
            Console.WriteLine("   - ▓ 50 to 75 msecs");
            Console.WriteLine("   - █ 75 msecs or longer");

            Console.WriteLine(new string('#', 25) + "\n");
        }

        private static object ConsoleLock = new object();
        private static void StartProcessingEvents()
        {
            // See https://github.com/dotnet/coreclr/blob/775003a4c72f0acc37eab84628fcef541533ba4e/src/pal/prebuilt/inc/mscoree.h#L294-L315
            session.Source.Clr.RuntimeStart += runtimeData =>
            {
                if (ProcessIdsUsedInRuns.Contains(runtimeData.ProcessID) == false)
                    return;

                lock (ConsoleLock)
                {
                    Console.WriteLine("\nCONCURRENT_GC = {0}, SERVER_GC = {1}",
                                    (runtimeData.StartupFlags & StartupFlags.CONCURRENT_GC) == StartupFlags.CONCURRENT_GC,
                                    (runtimeData.StartupFlags & StartupFlags.SERVER_GC) == StartupFlags.SERVER_GC);
                }
            };

            session.Source.Clr.GCAllocationTick += allocationData =>
            {
                if (ProcessIdsUsedInRuns.Contains(allocationData.ProcessID) == false)
                    return;

                totalBytesAllocated += allocationData.AllocationAmount;

                lock (ConsoleLock)
                {
                    Console.Write(".");
                }
            };

            GCType lastGCType = 0;
            session.Source.Clr.GCStart += gcData =>
            {
                if (ProcessIdsUsedInRuns.Contains(gcData.ProcessID) == false)
                    return;

                var colourToUse = GetColourForGC(gcData.Depth);
                lastGCType = gcData.Type;

                lock (ConsoleLock)
                {
                    if (gcData.Type == GCType.ForegroundGC || gcData.Type == GCType.NonConcurrentGC)
                    {
                        // Make the FG coloured/highlighted
                        Console.ForegroundColor = colourToUse;
                        Console.BackgroundColor = ConsoleColor.Black;
                    }
                    else // GCType.BackgroundGC
                    {
                        // Make the BG coloured/highlighted
                        Console.ForegroundColor = ConsoleColor.Black;
                        Console.BackgroundColor = colourToUse;
                    }

                    IncrementGCCollectionCounts(gcData);

                    Console.Write(gcData.Depth);
                    Console.ResetColor();
                }
            };

            //In a typical blocking GC (this means all ephemeral GCs and full blocking GCs) the event sequence is very simple:
            //GCSuspendEE_V1 Event
            //GCSuspendEEEnd_V1 Event    <– suspension is done
            //GCStart_V1 Event
            //GCEnd_V1 Event             <– actual GC is done
            //GCRestartEEBegin_V1 Event
            //GCRestartEEEnd_V1 Event    <– resumption is done.

            // When NOT a B/G GC, Suspension MSec column is simply (timestamp of the GCSuspendEEEnd_V1 – timestamp of the GCSuspendEE_V1).

            double pauseStart = 0;
            session.Source.Clr.GCSuspendEEStop += suspendData =>
            {
                if (ProcessIdsUsedInRuns.Contains(suspendData.ProcessID) == false)
                    return;

                pauseStart = suspendData.TimeStampRelativeMSec;
            };

            session.Source.Clr.GCRestartEEStop += restartData =>
            {
                if (ProcessIdsUsedInRuns.Contains(restartData.ProcessID) == false)
                    return;

                // Only display this if the GC Type is Foreground, (Background is different!!)
                // 0x0 - NonConcurrentGC - Blocking garbage collection occurred outside background garbage collection.
                // 0x1 - BackgroundGC    - Background garbage collection.
                // 0x2 - ForegroundGC    - Blocking garbage collection occurred during background garbage collection.
                if (lastGCType == GCType.BackgroundGC)
                    return;

                var pauseDurationMSec = restartData.TimeStampRelativeMSec - pauseStart;
                var pauseText = new StringBuilder();
                while (pauseDurationMSec > 100)
                {
                    pauseText.Append('█');
                    pauseDurationMSec -= 100;
                }
                if (pauseDurationMSec > 75)
                    pauseText.Append('█');
                else if (pauseDurationMSec > 50)
                    pauseText.Append('▓');
                else if (pauseDurationMSec > 25)
                    pauseText.Append('▒');
                else
                    pauseText.Append("░");

                //pauseText.AppendFormat("({0:N2} ms)", restartData.TimeStampRelativeMSec - pauseStart);

                lock (ConsoleLock)
                {
                    Console.ResetColor();
                    Console.Write(pauseText.ToString());
                }          
            };

            session.Source.Process();
        }

        private static void IncrementGCCollectionCounts(GCStartTraceData gcData)
        {
            if (gcData.Type == GCType.BackgroundGC && gcData.Depth == 2)
                gen2Background++;
            else if (gcData.Depth == 0)
                gen0++;
            else if (gcData.Depth == 1)
                gen1++;
            else if (gcData.Depth == 2)
                gen2++;
            else if (gcData.Depth == 3)
                gen3++; // L.O.H
        }

        private static ConsoleColor GetColourForGC(int depth)
        {
            var colourToUse = ConsoleColor.White;
            if (depth == 0)
                colourToUse = ConsoleColor.Yellow;
            else if (depth == 1)
                colourToUse = ConsoleColor.Blue;
            else if (depth == 2)
                colourToUse = ConsoleColor.Red;
            else
                colourToUse = ConsoleColor.Green;

            return colourToUse;
        }
    }
}
