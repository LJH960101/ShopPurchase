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
    /// 새 플랫폼을 추가할 때 이 클래스를 손댈 필요가 없다 (IPlatform 구현 + GetPlatformEnum()만 있으면 됨).
    /// </summary>
    public class PlatformManager
    {
        public static readonly PlatformManager Instance = new PlatformManager();

        private readonly Dictionary<EPlatform, IPlatform> m_platforms;

        private PlatformManager()
        {
            m_platforms = BuildPlatforms();
        }

        // 플랫폼이 추가될 때마다 이 클래스에 등록 코드를 직접 추가해야 하는 비효율적인 코드가 늘어나는 것을
        // 최소화하기 위해, 리플렉션을 사용하여 IPlatform 구현 클래스만 추가하면 자동으로 대응되도록 작업하였습니다.
        private static Dictionary<EPlatform, IPlatform> BuildPlatforms()
        {
            var implementationTypes = typeof(IPlatform).Assembly
                .GetTypes()
                .Where(_type => typeof(IPlatform).IsAssignableFrom(_type) && !_type.IsInterface && !_type.IsAbstract);

            var platforms = new Dictionary<EPlatform, IPlatform>();
            foreach (var type in implementationTypes)
            {
                var instance = (IPlatform)Activator.CreateInstance(type);
                platforms[instance.GetPlatformEnum()] = instance;
            }

            return platforms;
        }

        public IPlatform GetPlatform(EPlatform _platform)
        {
            // 여기 걸리면 EPlatform 값 하나에 대응하는 IPlatform 구현체가 아예 없다는 뜻 — 설정/배포 누락
            // 같은 진짜 버그다. 정상적인 실패(EErrorCode.ReceiptVerifyFailed 등)와는 성격이 달라서
            // ErrorCode로 흘려보내지 않고, 즉시 예외로 드러낸다.
            if (!m_platforms.TryGetValue(_platform, out var impl))
                throw new InvalidOperationException($"Unsupported platform (등록된 IPlatform 구현체 없음): {_platform}");

            return impl;
        }

        /// <summary>주어진 플랫폼의 검증 전략을 찾아 영수증을 검증한다.</summary>
        public JHJob<bool> Verify(EPlatform _platform, string _receipt)
        {
            return GetPlatform(_platform).Verify(_receipt);
        }
    }
}
