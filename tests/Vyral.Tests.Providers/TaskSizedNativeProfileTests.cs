using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Vyral.Providers.Abstractions;
using Vyral.Providers.Local;

namespace Vyral.Tests.Providers;

public class TaskSizedNativeProfileTests
{
    private static NativeProfile Profile(int context, int output) => new() {
        Endpoint="http://127.0.0.1:1/",ModelId="not-opened",ModelPath="/synthetic-not-read/model.gguf",
        ModelSha256=new('a',64),BuildInfo="fixture",TemplateSha256=new('b',64),TemplateProbeSha256=new('c',64),
        ContextTokens=context,MaxOutputTokens=output,MaxResponseBytes=1048576 };

    [Theory]
    [InlineData(4096,512)] [InlineData(4096,1024)] [InlineData(8192,2048)] [InlineData(8192,4096)]
    [InlineData(16384,5120)] [InlineData(32768,8192)] [InlineData(1048576,8192)]
    public void SharedControllerProfileAndPublicFactoryAdmitBoundedContextWithExactOutput(int context,int output)
    {
        var options=Profile(context,output).ToOptions(); var factory=new LlamaCppRuntimeSessionFactory(options);
        var target=new LocalLlamaCppProviderTarget(options,1800);
        Assert.Equal(context, target.Capabilities[0].InputLimits["maxContextTokens"]);
        Assert.Equal(output,target.Capabilities[0].OutputLimits["maxOutputTokens"]);
        Assert.Equal(factory.BindingId,target.Profile.ConfigHash);
        Assert.Equal("text", options.ChatOutputFormat);
        Assert.Equal(1800, target.MaxExecutionSeconds);
    }

    [Theory]
    [InlineData(1,1)] [InlineData(1048577,2048)]
    [InlineData(8192,0)] [InlineData(8192,8192)] [InlineData(8192,8193)]
    public void ControllerContextOrFactoryOutputExcessRefusesBeforeRuntime(int context,int output) =>
        Assert.Throws<ArgumentException>(() => new LlamaCppRuntimeSessionFactory(Profile(context,output).ToOptions()));

    [Fact]
    public void DifferentContextOrOutputChangesExactNativeBinding()
    {
        var legacy=new LlamaCppRuntimeSessionFactory(Profile(4096,1024).ToOptions()).BindingId;
        var larger=new LlamaCppRuntimeSessionFactory(Profile(8192,2048).ToOptions()).BindingId;
        Assert.NotEqual(legacy,larger);
        Assert.NotEqual(larger,new LlamaCppRuntimeSessionFactory(Profile(8192,4096).ToOptions()).BindingId);
        var configured = new LlamaCppRuntimeSessionFactory(Profile(16384,5120).ToOptions()).BindingId;
        Assert.NotEqual(larger,configured);
        Assert.NotEqual(configured,new LlamaCppRuntimeSessionFactory(Profile(16384,4096).ToOptions()).BindingId);
        Assert.Equal(larger,new LocalLlamaCppProviderTarget(Profile(8192,2048).ToOptions(),3600).Profile.ConfigHash);
        Assert.Equal(120,new LocalLlamaCppProviderTarget(Profile(8192,2048).ToOptions()).MaxExecutionSeconds);
    }

    [Theory]
    [InlineData(8192,2048)] [InlineData(8192,4096)] [InlineData(16384,5120)]
    public async Task ActualSessionTokenGuardAllowsExactBoundaryAndRefusesOneMoreWithoutSecondSend(int context,int output)
    {
        // In-memory transport exercises the real native session; no native process or socket.
        var handler = new CompletionHandler();
        await using var session = new LlamaCppRuntimeSession(Profile(context,output).ToOptions(),handler);
        var exact = new LocalScoringPrompt(Enumerable.Range(1,context-output).ToArray());
        await session.CompleteAsync(exact,output,false,default);
        Assert.Equal(1,handler.Sends);
        var excess = new LocalScoringPrompt(Enumerable.Range(1,context+1-output).ToArray());
        var held = await Assert.ThrowsAsync<LocalScoringException>(() => session.CompleteAsync(excess,output,false,default));
        Assert.Equal("context_limit_exceeded",held.Message);
        Assert.Equal(ProviderFailureClasses.Policy,held.FailureClass);
        Assert.Equal(1,handler.Sends);
    }

    [Theory]
    [InlineData(16384)] [InlineData(8192)]
    public async Task RuntimePropertiesMustMatchConfiguredCapacityBeforePreparation(int reportedContext)
    {
        // Test-only in-memory HTTP seam and fake bytes; no model, socket or process.
        var model = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(model,"synthetic bytes, not a model");
            static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            var options = (Profile(16384,5120) with {
                ModelPath=model,ModelSha256=Hash("synthetic bytes, not a model"),
                TemplateSha256=Hash("template"),TemplateProbeSha256=Hash("probe") }).ToOptions();
            var handler = new PropertiesHandler(model,reportedContext);
            var factory = new LlamaCppRuntimeSessionFactory(options,() => handler);
            if (reportedContext != options.ContextTokens)
            {
                var held = await Assert.ThrowsAsync<LocalScoringException>(async () => await factory.OpenAsync(default));
                Assert.Equal("runtime_binding_mismatch",held.Message);
                Assert.Equal(ProviderFailureClasses.Configuration,held.FailureClass);
                Assert.Equal(0,handler.TemplateRequests);
            }
            else
            {
                await using var session = await factory.OpenAsync(default);
                Assert.Equal(16384,session.ContextTokens);
                Assert.Equal(1,handler.TemplateRequests);
            }
            Assert.Equal(0,handler.Completions);
        }
        finally { File.Delete(model); }
    }

    private sealed class PropertiesHandler(string model,int context) : HttpMessageHandler
    {
        public int TemplateRequests { get; private set; }
        public int Completions { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            JsonObject output;
            if (request.RequestUri!.AbsolutePath == "/props") output = new() {
                ["model_path"]=model,["build_info"]="fixture",["chat_template"]="template",
                ["default_generation_settings"]=new JsonObject { ["n_ctx"]=context },["total_slots"]=1 };
            else if (request.RequestUri.AbsolutePath == "/apply-template")
            { TemplateRequests++; output = new() { ["prompt"]="probe" }; }
            else { Completions++;throw new InvalidOperationException("unexpected runtime path"); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content=new StringContent(output.ToJsonString(),Encoding.UTF8,"application/json") });
        }
    }

    private sealed class CompletionHandler : HttpMessageHandler
    {
        public int Sends { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Assert.Equal("/completion",request.RequestUri!.AbsolutePath);Sends++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content=new StringContent("{}",Encoding.UTF8,"application/json") });
        }
    }
}
