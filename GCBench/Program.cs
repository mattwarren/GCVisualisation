// This benchmark has been modified (by Will Clinger):
//
//      The name of the main class has been changed to GCBench.
//      The name of the main method has been changed to originalMain.
//      The benchmark's parameters are now variables instead of constants.
//      A new main method allows the number of iterations and the
//           benchmark's main size parameter to be specified on the
//           command line.  The new main method computes the other
//           parameters from the main parameter, times the iterated
//           benchmark, and reports that iterated timing in addition
//           to timings for each iteration (which are still reported
//           by the originalMain method).
//
// Usage:
//
//      java GCBench N K
//
// where
//      N is the number of iterations (defaulting to its original value, 1)
//      K is the size parameter (defaulting to its original value, 18)
//
// The original comment follows:

// This is adapted from a benchmark written by John Ellis and Pete Kovac
// of Post Communications.
// It was modified by Hans Boehm of Silicon Graphics.
//
//      This is no substitute for real applications.  No actual application
//      is likely to behave in exactly this way.  However, this benchmark was
//      designed to be more representative of real applications than other
//      Java GC benchmarks of which we are aware.
//      It attempts to model those properties of allocation requests that
//      are important to current GC techniques.
//      It is designed to be used either to obtain a single overall performance
//      number, or to give a more detailed estimate of how collector
//      performance varies with object lifetimes.  It prints the time
//      required to allocate and collect balanced binary trees of various
//      sizes.  Smaller trees result in shorter object lifetimes.  Each cycle
//      allocates roughly the same amount of memory.
//      Two data structures are kept around during the entire process, so
//      that the measured performance is representative of applications
//      that maintain some live in-memory data.  One of these is a tree
//      containing many pointers.  The other is a large array containing
//      double precision floating point numbers.  Both should be of comparable
//      size.
//
//      The results are only really meaningful together with a specification
//      of how much memory was used.  It is possible to trade memory for
//      better time performance.  This benchmark should be run in a 32 MB
//      heap, though we don't currently know how to enforce that uniformly.
//
//      Unlike the original Ellis and Kovac benchmark, we do not attempt
//      measure pause times.  This facility should eventually be added back
//      in.  There are several reasons for omitting it for now.  The original
//      implementation depended on assumptions about the thread scheduler
//      that don't hold uniformly.  The results really measure both the
//      scheduler and GC.  Pause time measurements tend to not fit well with
//      current benchmark suites.  As far as we know, none of the current
//      commercial Java implementations seriously attempt to minimize GC pause
//      times.

// Simple conversion to C# - leppie
using System;
using System.Threading.Tasks;

class Node
{
    public Node left, right;
    
    public Node(Node l, Node r) : this()
    {
        left = l;
        right = r;
    }
    public Node() {}
}

class GCBench
{
    public static void Main(string[] args)
    {
        int n = Environment.ProcessorCount;                // number of iterations

        if (args.Length > 0)
            n = int.Parse(args[0]);
        if (args.Length > 1)
        {
            kStretchTreeDepth = int.Parse(args[1]);
            kLongLivedTreeDepth = kStretchTreeDepth - 2;
            kArraySize = 4 * TreeSize(kLongLivedTreeDepth);
            kMaxTreeDepth = kLongLivedTreeDepth;
        }
        if (n == 1)
            originalMain(0);
        else
        {
            long tStart, tFinish;
            tStart = DateTime.UtcNow.Ticks;

            Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, n >> 1) }, originalMain);
            tFinish = DateTime.UtcNow.Ticks;
            PrintDiagnostics();
            Console.WriteLine($"{n} gcbench:{kStretchTreeDepth} took {new TimeSpan(tFinish - tStart).TotalMilliseconds:F0} ms.");
        }
    }

    public static int kStretchTreeDepth = 20;  // about 16Mb
    public static int kLongLivedTreeDepth = 18;  // about 16Mb
    public static int kArraySize = 4000000;       // about 16Mb
    public const int kMinTreeDepth = 10;
    public static int kMaxTreeDepth = 16;

    // Nodes used by a tree of a given size
    static int TreeSize(int i)
    {
        return ((1 << (i + 1)) - 1);
    }

    // Number of iterations to use for a given tree depth
    static int NumIters(int i)
    {
        return 2 * TreeSize(kStretchTreeDepth) / TreeSize(i);
    }

    // Build tree top down, assigning to older objects.
    static void Populate(int iDepth, Node thisNode)
    {
        if (iDepth <= 0)
        {
            return;
        }
        else
        {
            iDepth--;
            thisNode.left = new Node();
            thisNode.right = new Node();
            Populate(iDepth, thisNode.left);
            Populate(iDepth, thisNode.right);
        }
    }

    // Build tree bottom-up
    static Node MakeTree(int iDepth)
    {
        if (iDepth <= 0)
        {
            return new Node();
        }
        else
        {
            return new Node(MakeTree(iDepth - 1),
                            MakeTree(iDepth - 1));
        }
    }

    static void PrintDiagnostics()
    {
        GC.Collect(2, GCCollectionMode.Optimized, false);
        Console.WriteLine($" Working set={Environment.WorkingSet:N0} bytes");
    }

    static void TimeConstruction(int depth)
    {
        Node root;
        long tStart, tFinish;
        int iNumIters = NumIters(depth);
        Node tempTree;

        Console.WriteLine("Creating " + iNumIters +
                           " trees of depth " + depth);
        tStart = DateTime.UtcNow.Ticks;
        for (int i = 0; i < iNumIters; ++i)
        {
            tempTree = new Node();
            Populate(depth, tempTree);
            tempTree = null;
        }
        tFinish = DateTime.UtcNow.Ticks;
        Console.WriteLine($"\tTop down construction took {new TimeSpan(tFinish - tStart).TotalMilliseconds:F0} ms");
        tStart = DateTime.UtcNow.Ticks;
        for (int i = 0; i < iNumIters; ++i)
        {
            tempTree = MakeTree(depth);
            tempTree = null;
        }
        tFinish = DateTime.UtcNow.Ticks;
        Console.WriteLine($"\tBottom up construction took {new TimeSpan(tFinish - tStart).TotalMilliseconds:F0} ms");
    }

    public static void originalMain(int cpu)
    {
        Node longLivedTree;
        Node tempTree;
        long tStart, tFinish;

        Console.WriteLine("Garbage Collector Test");
        Console.WriteLine(
                " Stretching memory with a binary tree of depth "
                + kStretchTreeDepth);
        PrintDiagnostics();
        tStart = DateTime.UtcNow.Ticks;

        // Stretch the memory space quickly
        tempTree = MakeTree(kStretchTreeDepth);
        tempTree = null;

        // Create a long lived object
        Console.WriteLine(
                " Creating a long-lived binary tree of depth " +
                kLongLivedTreeDepth);
        longLivedTree = new Node();
        Populate(kLongLivedTreeDepth, longLivedTree);

        // Create long-lived array, filling half of it
        Console.WriteLine(
                " Creating a long-lived array of "
                + kArraySize + " doubles");
        double[] array = new double[kArraySize];
        for (int i = 0; i < kArraySize / 2; ++i)
        {
            array[i] = 1.0 / i;
        }
        PrintDiagnostics();

        for (int d = kMinTreeDepth; d <= kMaxTreeDepth; d += 2)
        {
            TimeConstruction(d);
        }

        if (longLivedTree == null || array[1000] != 1.0 / 1000)
            Console.WriteLine("Failed");
        // fake reference to LongLivedTree
        // and array
        // to keep them from being optimized away
        longLivedTree = null;
        tFinish = DateTime.UtcNow.Ticks;
        PrintDiagnostics();
        Console.WriteLine($"Completed in {new TimeSpan(tFinish - tStart).TotalMilliseconds:F0} ms.");

    }
} // class JavaGC