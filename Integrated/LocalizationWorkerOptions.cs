namespace Integrated.Runtime
{
    public sealed record LocalizationWorkerOptions
    {
        public int? InferenceTimeoutMs { get; init; }
        public int CooldownAfterFailureMs { get; init; } = 2000;
    }
}
