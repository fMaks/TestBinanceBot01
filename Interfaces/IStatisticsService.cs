using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

public interface IStatisticsService
{
    long GetProcessedCount();
    void IncrementProcessedCount(int count = 1);
}
