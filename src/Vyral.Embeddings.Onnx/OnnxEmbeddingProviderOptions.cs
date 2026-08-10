namespace Vyral.Embeddings.Onnx;

public enum OnnxExecutionProviderPreference
{
    Cpu,
    CudaPreferred,
    CudaRequired
}

public enum OnnxPoolingMode
{
    Mean,
    Cls
}

public enum OnnxCrossEncoderScoreMode
{
    Auto,
    Logit,
    Sigmoid,
    SoftmaxFirst,
    SoftmaxSecond
}
