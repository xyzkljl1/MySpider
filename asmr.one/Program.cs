using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
