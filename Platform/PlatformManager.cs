using System;
using System.Collections.Generic;
using System.Linq;
using ShopPurchase.Common;
using ShopPurchase.Core.Thread;

namespace ShopPurchase.Platform
{
    /// <summary>
    /// EPlatform -> IPlatform 전략 선택 + 검증 호출을 담당한다.
    /// IPlatform 구현체를 어셈블리에서 리플렉션으로 스캔해 자동으로 등록하므로,
    /// 새 플랫폼을 추가할 때 이 클래스를 손댈 필요가 없다 (IPlatform 구현 + GetPlatformType()만 있으면 됨).
    /// </summary>
    public class PlatformManager
    {
        public static readonly PlatformManager Instance = new PlatformManager();

        private readonly Dictionary<EPlatform, IPlatform> m_platforms;

        private PlatformManager()
        {
            m_platforms = BuildPlatforms();
        }

        // 플랫폼이 추가될 때마다 이 클래스에 등록 코드를 직접 추가해야 하는 번거로움을 없애기 위해,
        // 리플렉션으로 IPlatform 구현 클래스를 스캔해서 자동으로 등록한다.
        private static Dictionary<EPlatform, IPlatform> BuildPlatforms()
        {
            var implementationTypes = typeof(IPlatform).Assembly
                .GetTypes()
                .Where(_type => typeof(IPlatform).IsAssignableFrom(_type) && !_type.IsInterface && !_type.IsAbstract);

            var platforms = new Dictionary<EPlatform, IPlatform>();
            foreach (var type in implementationTypes)
            {
                var instance = (IPlatform)Activator.CreateInstance(type);
                platforms[instance.GetPlatformType()] = instance;
            }

            return platforms;
        }

        public IPlatform GetPlatform(EPlatform _platform)
        {
            return m_platforms.TryGetValue(_platform, out var impl) ? impl : null;
        }

        public JHJob<VerifiedReceipt> Verify(EPlatform _platform, string _receipt, int _expectedProductId)
        {
            var platform = GetPlatform(_platform);
            if (platform == null)
                return JHJob<VerifiedReceipt>.Rejected(EErrorCode.UnsupportedPlatform);

            return platform.Verify(_receipt)
                .Then(_verified => _verified.ProductId == _expectedProductId
                    ? JHJob<VerifiedReceipt>.Resolved(_verified)
                    : JHJob<VerifiedReceipt>.Rejected(EErrorCode.ReceiptProductMismatch));
        }
    }
}
