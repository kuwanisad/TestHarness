namespace TestHarness.Core;

/// <summary>
/// 하네스 기능을 쓰는 쪽(AI, 시나리오)이 고칠 수 있는 실패 - 잘못된 인자, 순서 위반(서버가 떠 있는데
/// 빌드), 기다리던 것이 안 옴 등. Hint에는 "그럼 무엇을 하면 되는지"를 한 줄로 적는다 - AI가 같은 실수를
/// 반복하지 않고 바로 다음 행동을 고를 수 있게.
/// </summary>
public sealed class HarnessException : Exception
{
    public HarnessException(string message, string? hint = null, Exception? inner = null)
        : base(message, inner)
    {
        Hint = hint;
    }

    public string? Hint { get; }
}
