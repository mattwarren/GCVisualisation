using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Session;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace GCVisualisation
{
    class Program
    {
        private static TraceEventSession session;
        private static ConcurrentBag<int> ProcessIdsUsedInRuns = new ConcurrentBag<int>();

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
            //startInfo.Arguments = file;
            var process = Process.Start(startInfo);
            ProcessIdsUsedInRuns.Add(process.Id);
            process.Exited += (sender, e) => Console.WriteLine("\nProcess has exited\n");
            Console.CancelKeyPress += (sender, e) =>
            {
                Console.WriteLine("\nConsole being killed, tidying up\n");
                session.Dispose();
            };

            PrintSymbolInformation();
            

            Console.WriteLine("Visualising GC Events, press <ENTER> to exit"); 
            Console.ReadLine();

            if (process.HasExited == false)
                process.Kill();
            session.Dispose();
        }

        private static void PrintSymbolInformation()
        {
            Console.WriteLine("Key to symbols:");
            Console.WriteLine(" - '.'   represents ~100K of memory allocations");

            Console.Write(" - ");
            Console.ForegroundColor = GetColourForGC(0); Console.Write("0");
            Console.ResetColor(); Console.Write("/");
            Console.ForegroundColor = GetColourForGC(1); Console.Write("1");
            Console.ResetColor(); Console.Write("/");
            Console.ForegroundColor = GetColourForGC(2); Console.Write("2");
            Console.ResetColor();
            Console.WriteLine(" indicates a FOREGROUND GC Collection, for the given generation (0, 1 or 2)");

            Console.Write(" - ");
            Console.BackgroundColor = GetColourForGC(2); Console.ForegroundColor = ConsoleColor.Black; Console.Write("2");
            Console.ResetColor();
            Console.WriteLine("     indicates a BACKGROUND GC Collection (Gen 2 only)");

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

                    Console.Write(gcData.Depth);
                    Console.ResetColor();
                }
            };

            session.Source.Process();
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
