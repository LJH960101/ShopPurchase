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

        /// <summary>등록된 구현체가 없으면(설정/배포 누락) null.</summary>
        public IPlatform GetPlatform(EPlatform _platform)
        {
            return m_platforms.TryGetValue(_platform, out var impl) ? impl : null;
        }

        /// <summary>
        /// 주어진 플랫폼의 검증 전략을 찾아 영수증을 검증하고, 그 영수증이 실제로 _expectedProductId
        /// 상품에 대한 것인지까지 대조한다. 등록된 구현체가 없으면(설정/배포 누락) 예외 대신
        /// EErrorCode.UnsupportedPlatform으로 reject한다.
        ///
        /// 상품 대조를 호출자(PacketHandler)가 아니라 이 파사드 안에 둔 이유: "영수증이 유효한가"와
        /// "그게 이 사람이 요청한 상품이 맞는가"는 다른 질문인데, 후자를 빠뜨려도 전자만으로 흐름이
        /// 멀쩡히 성공해버린다 — 싼 상품 영수증으로 비싼 상품을 받아가는 변조가 조용히 통과한다.
        /// 그래서 "잊어버릴 수 있는 검사"로 두지 않고, 검증을 부르면 반드시 같이 수행되도록 기대
        /// 상품 ID를 인자로 받게 만들었다.
        /// </summary>
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
