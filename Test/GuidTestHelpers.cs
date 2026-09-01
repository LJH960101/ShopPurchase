using System;
using System.Collections.Generic;
using System.Linq;

namespace ShopPurchase.Test
{
    /// <summary>GuidGeneratorTest/BulkGrantTest가 공통으로 쓰는 "생성/유일/중복 개수" 출력 로직.</summary>
    internal static class GuidTestHelpers
    {
        public static void PrintDuplicateStats(IReadOnlyCollection<GUID> _ids)
        {
            int totalCount = _ids.Count;
            int distinctCount = _ids.Distinct().Count();
            int duplicateCount = totalCount - distinctCount;
            double duplicateRate = totalCount == 0 ? 0 : duplicateCount * 100.0 / totalCount;

            Console.WriteLine($"생성 개수: {totalCount}, 유일 개수: {distinctCount}, 중복 개수: {duplicateCount} ({duplicateRate:F2}%)");
        }
    }
}
