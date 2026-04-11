namespace asmr.one
{
    class Program
    {
        static void Main(string[] args)
        {
            using (BlockSyncContext.Enter())
            {
                var f = new Fetcher();
                f.Start().Wait();
            }
        }
    }
}
