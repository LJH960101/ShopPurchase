using System.Linq;

namespace ShopPurchase.Common
{
    public enum EErrorCode
    {
        Success,
        UnsupportedPlatform,
        InvalidParam,
        ReceiptVerifyFailed,
        ReceiptProductMismatch,
        ReceiptAlreadyInserted,
        DBConnectionFailed,
        InsertReceiptFailed,
        UpdateItemFailed,
        Exception,
    }

    public enum EPlatform
    {
        GooglePlay,
        AppStore,
        Steam,
    }

    public enum ECurrencyType
    {
        Gold,
    }

    public static class EErrorCodeExtensions
    {
        public static bool IsOneOf(this EErrorCode _errorCode, params EErrorCode[] _candidates)
        {
            return _candidates.Contains(_errorCode);
        }
    }
}
