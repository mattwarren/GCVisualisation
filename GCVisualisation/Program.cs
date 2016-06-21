using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Session;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GCVisualisation
{
    class Program
    {
        private static TraceEventSession session;
        private static ConcurrentBag<int> ProcessIdsUsedInRuns = new ConcurrentBag<int>();
        private static long totalBytesAllocated, gen0, gen1, gen2, gen2Background, gen3;
        private static double timeInGc, totalGcPauseTime, largestGcPause, startTime, stopTime;

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

            // The ETW collection thread starts receiving all events immediately, but we filter on the Process Id
            var processingTask = Task.Factory.StartNew(StartProcessingEvents, TaskCreationOptions.LongRunning);

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = args[0];
            var process = Process.Start(startInfo);
            ProcessIdsUsedInRuns.Add(process.Id);
            process.Exited += (sender, e) => Console.WriteLine("\nProcess has exited\n");
            Console.CancelKeyPress += (sender, e) =>
            {
                Console.WriteLine("\nConsole being killed, tidying up\n");
                session.Dispose();
                if (process.HasExited == false)
                    process.Kill();
                Console.WriteLine();
            };

            PrintSymbolInformation();
            
            Console.WriteLine("Visualising GC Events, press <ENTER>, <ESC> or 'q' to exit");
            Console.WriteLine("You can also push 's' at any time and the current summary will be displayed");
            ConsoleKeyInfo cki;
            while (process.HasExited == false)
            {
                if (Console.KeyAvailable == false)
                {
                    Thread.Sleep(250);
                    continue;
                }

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

            if (process.HasExited == false)
                process.Kill();

            // Flush the session before we finish, so that we get all the events possible
            session.Flush();
            // wait a little while for all events to come through (Flush() doesn't seem to do this?)
            Thread.Sleep(3000);
            // Now kill the session completely
            session.Dispose();
            
            var completed = processingTask.Wait(millisecondsTimeout: 3000);
            if (!completed)
                Console.WriteLine("\nWait timed out, the Processing Task is still running");

            PrintSummaryInfo();
            Console.WriteLine();
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

        private static void PrintSummaryInfo()
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;

            Console.WriteLine("\nMemory Allocations:");
            // 100GB = 107,374,182,400 bytes, so min-width = 15
            Console.WriteLine("  {0,15:N0} bytes currently allocated", GC.GetTotalMemory(forceFullCollection: false));
            Console.WriteLine("  {0,15:N0} bytes have been allocated in total", totalBytesAllocated);

            var totalGC = gen0 + gen1 + gen2 + gen3;
            var testTime = stopTime - startTime;
            Console.WriteLine("GC Collections:\n  {0:N0} in total ({1:N0} excluding B/G)", totalGC + gen2Background, totalGC);
            Console.WriteLine("  {0,4:N0} - generation 0", gen0);
            Console.WriteLine("  {0,4:N0} - generation 1", gen1);
            Console.WriteLine("  {0,4:N0} - generation 2", gen2);
            Console.WriteLine("  {0,4:N0} - generation 2 (B/G)", gen2Background);
            if (gen3 > 0)
                Console.WriteLine("  {0,4:N0} - generation 3 (LOH)", gen3);

            Console.WriteLine("Time in GC  : {0,12:N2} ms ({1:N2} ms avg per/GC) ", timeInGc, timeInGc / totalGC);
            Console.WriteLine("Time in test: {0,12:N2} ms ({1:P2} spent in GC)", testTime, timeInGc / testTime);
            Console.WriteLine("Total GC Pause time  : {0,12:N2} ms", totalGcPauseTime);
            Console.WriteLine("Largest GC Pause time: {0,12:N2} ms", largestGcPause);

            Console.ResetColor();
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

                if (startTime == 0)
                    startTime = allocationData.TimeStampRelativeMSec;

                stopTime = allocationData.TimeStampRelativeMSec;

                totalBytesAllocated += allocationData.AllocationAmount;

                lock (ConsoleLock)
                {
                    Console.Write(".");
                }
            };

            GCType lastGCType = 0;
            double gcStart = 0;
            session.Source.Clr.GCStart += startData =>
            {
                if (ProcessIdsUsedInRuns.Contains(startData.ProcessID) == false)
                    return;

                if (startTime == 0)
                    startTime = startData.TimeStampRelativeMSec;

                lastGCType = startData.Type;
                gcStart = startData.TimeStampRelativeMSec;
                IncrementGCCollectionCounts(startData);

                var colourToUse = GetColourForGC(startData.Depth);
                lock (ConsoleLock)
                {
                    if (startData.Type == GCType.ForegroundGC || startData.Type == GCType.NonConcurrentGC)
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

                    Console.Write(startData.Depth);
                    Console.ResetColor();
                }
            };

            session.Source.Clr.GCStop += stopData =>
            {
                if (ProcessIdsUsedInRuns.Contains(stopData.ProcessID) == false)
                    return;

                stopTime = stopData.TimeStampRelativeMSec;

                // If we don't have a matching start event, don't calculate the GC time
                if (gcStart == 0)
                    return;

                timeInGc += (stopData.TimeStampRelativeMSec - gcStart);
            };

            //In a typical blocking GC (this means all ephemeral GCs and full blocking GCs) the event sequence is very simple:
            //GCSuspendEE_V1 Event
            //GCSuspendEEEnd_V1 Event    <– suspension is done
            //GCStart_V1 Event
            //GCEnd_V1 Event             <– actual GC is done
            //GCRestartEEBegin_V1 Event
            //GCRestartEEEnd_V1 Event    <– resumption is done.

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

                stopTime = restartData.TimeStampRelativeMSec;

                // Only display this if the GC Type is Foreground, (Background is different!!)
                // 0x0 - NonConcurrentGC - Blocking garbage collection occurred outside background garbage collection.
                // 0x1 - BackgroundGC    - Background garbage collection.
                // 0x2 - ForegroundGC    - Blocking garbage collection occurred during background garbage collection.
                if (lastGCType == GCType.BackgroundGC)
                    return;

                // If we don't have a matching start event, don't calculate the "pause" time
                if (pauseStart == 0)
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

                totalGcPauseTime += pauseDurationMSec;
                if (pauseDurationMSec > largestGcPause)
                    largestGcPause = pauseDurationMSec;

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
