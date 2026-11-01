using System.Net.Http.Headers;
using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// STT/TTS via providers HTTP: OpenAI-compatible (/audio/speech,
/// /audio/transcriptions, /audio/voices, /audio/models), Deepgram (STT),
/// whisper externo (faster-whisper-server, OpenAI-compatível),
/// ElevenLabs e Azure Speech (TTS).
/// Sem provider configurado lança <see cref="AudioDisabledException"/> → 501.
/// Chaves ficam no servidor; o áudio do usuário não é persistido.
/// </summary>
public class AudioService(IHttpClientFactory httpFactory, ConfigService config)
{
    private const string ProviderKey = "provider";
    private const string ElevenLabsProvider = "elevenlabs";
    private const string DeepgramProvider = "deepgram";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Config ativa.</summary>
    /// <param name="ct">Cancelamento.</param>
    public Task<AudioConfig> GetConfigAsync(CancellationToken ct = default) =>
        config.GetAsync("audio.config", AudioConfig.Default, ct);

    /// <summary>Persiste a config.</summary>
    /// <param name="audio">Config a gravar.</param>
    /// <param name="ct">Cancelamento.</param>
    public Task SetConfigAsync(AudioConfig audio, CancellationToken ct = default) =>
        config.SetAsync("audio.config", audio, ct);

    /// <summary>
    /// Config resolvida: engine <c>provider</c> é convertida para <c>openai</c>
    /// com a URL base e a chave da conexão OpenAI cadastrada correspondente.
    /// Quando a conexão não é encontrada a engine cai para <c>none</c> (desabilitada).
    /// </summary>
    /// <param name="ct">Cancelamento.</param>
    public async Task<AudioConfig> GetResolvedConfigAsync(CancellationToken ct = default) =>
        await ResolveProvidersAsync(await GetConfigAsync(ct), ct);

    private async Task<AudioConfig> ResolveProvidersAsync(AudioConfig cfg, CancellationToken ct)
    {
        if (cfg.SttEngine != ProviderKey && cfg.TtsEngine != ProviderKey)
        {
            return cfg;
        }

        var connections = await config.GetConnectionsAsync(ct);

        (string Url, string? Key)? Find(string? providerUrl)
        {
            if (string.IsNullOrWhiteSpace(providerUrl))
            {
                return null;
            }
            var index = connections.OpenAiBaseUrls.ToList().FindIndex(u =>
                string.Equals(u?.TrimEnd('/'), providerUrl.TrimEnd('/'),
                    StringComparison.OrdinalIgnoreCase));
            return index < 0
                ? null
                : (connections.OpenAiBaseUrls[index],
                    connections.OpenAiApiKeys.ElementAtOrDefault(index));
        }

        if (cfg.SttEngine == ProviderKey)
        {
            cfg = Find(cfg.SttProvider) is { } stt
                ? cfg with { SttEngine = "openai", SttBaseUrl = stt.Url, SttApiKey = stt.Key }
                : cfg with { SttEngine = "none" };
        }
        if (cfg.TtsEngine == ProviderKey)
        {
            cfg = Find(cfg.TtsProvider) is { } tts
                ? cfg with { TtsEngine = "openai", TtsBaseUrl = tts.Url, TtsApiKey = tts.Key }
                : cfg with { TtsEngine = "none" };
        }
        return cfg;
    }

    /// <summary>
    /// Gera áudio a partir de texto no provider TTS configurado.
    /// </summary>
    /// <param name="input">Texto a sintetizar.</param>
    /// <param name="voice">Voz (null = config).</param>
    /// <param name="model">Modelo (null = config).</param>
    /// <param name="ct">Cancelamento.</param>
    /// <exception cref="AudioDisabledException">TTS desabilitado.</exception>
    /// <exception cref="AudioProviderException">Erro do provider.</exception>
    public async Task<SpeechResult> SpeechAsync(
        string input, string? voice, string? model, CancellationToken ct = default)
    {
        var cfg = await ResolveProvidersAsync(await GetConfigAsync(ct), ct);
        if (!cfg.TtsEnabled)
        {
            throw new AudioDisabledException("TTS desabilitado — configure um provider.");
        }

        var http = httpFactory.CreateClient(nameof(AudioService));
        http.Timeout = TimeSpan.FromSeconds(60);
        using var request = cfg.TtsEngine switch
        {
            ElevenLabsProvider => ElevenLabsRequest(cfg, input, voice, model),
            "azure" => AzureRequest(cfg, input, voice),
            _ => OpenAiSpeechRequest(cfg, input, voice, model),
        };

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new AudioProviderException(
                $"Provider TTS respondeu {(int)response.StatusCode}.");
        }

        return new SpeechResult(
            await response.Content.ReadAsByteArrayAsync(ct),
            response.Content.Headers.ContentType?.ToString() ?? "audio/mpeg");
    }

    /// <summary>POST {base}/audio/speech — OpenAI e transformers (openedai-speech).</summary>
    private static HttpRequestMessage OpenAiSpeechRequest(
        AudioConfig cfg, string input, string? voice, string? model)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{cfg.TtsBaseUrl!.TrimEnd('/')}/audio/speech");
        if (!string.IsNullOrEmpty(cfg.TtsApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.TtsApiKey);
        }
        request.Content = JsonContent.Create(new
        {
            model = model ?? cfg.TtsModel ?? "tts-1",
            voice = voice ?? cfg.TtsVoice ?? "alloy",
            input,
        }, options: JsonOptions);
        return request;
    }

    /// <summary>POST {base}/v1/text-to-speech/{voice} com xi-api-key.</summary>
    private static HttpRequestMessage ElevenLabsRequest(
        AudioConfig cfg, string input, string? voice, string? model)
    {
        var voiceId = voice ?? cfg.TtsVoice
            ?? throw new AudioDisabledException("ElevenLabs exige voice_id configurado.");
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{ElevenLabsBase(cfg)}/v1/text-to-speech/{Uri.EscapeDataString(voiceId)}");
        request.Headers.TryAddWithoutValidation("xi-api-key", cfg.TtsApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));
        request.Content = JsonContent.Create(new
        {
            text = input,
            model_id = model ?? cfg.TtsModel ?? "eleven_multilingual_v2",
        }, options: JsonOptions);
        return request;
    }

    /// <summary>POST {base}/cognitiveservices/v1 — SSML + Ocp-Apim-Subscription-Key.</summary>
    private static HttpRequestMessage AzureRequest(
        AudioConfig cfg, string input, string? voice)
    {
        var voiceName = voice ?? cfg.TtsVoice ?? "en-US-AriaNeural";
        var ssml =
            "<speak version='1.0' xml:lang='en-US'>" +
            $"<voice xml:lang='en-US' name='{voiceName}'>" +
            $"{System.Security.SecurityElement.Escape(input)}</voice></speak>";
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{AzureBase(cfg)}/cognitiveservices/v1");
        request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", cfg.TtsApiKey);
        request.Headers.TryAddWithoutValidation(
            "X-Microsoft-OutputFormat", "audio-16khz-128kbitrate-mono-mp3");
        request.Headers.TryAddWithoutValidation("User-Agent", "open-webui");
        request.Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml");
        return request;
    }

    private static string ElevenLabsBase(AudioConfig cfg) =>
        string.IsNullOrWhiteSpace(cfg.TtsBaseUrl)
            ? "https://api.elevenlabs.io"
            : cfg.TtsBaseUrl.TrimEnd('/');

    private static string AzureBase(AudioConfig cfg) =>
        !string.IsNullOrWhiteSpace(cfg.TtsBaseUrl)
            ? cfg.TtsBaseUrl.TrimEnd('/')
            : $"https://{cfg.AzureRegion}.tts.speech.microsoft.com";

    /// <summary>Aplica o header de auth correto da engine (Bearer, Token ou chaves próprias).</summary>
    private static void AddAuthHeader(HttpRequestMessage request, AudioConfig cfg, bool isStt)
    {
        var key = isStt ? cfg.SttApiKey : cfg.TtsApiKey;
        if (string.IsNullOrEmpty(key))
        {
            return;
        }
        var engine = isStt ? cfg.SttEngine : cfg.TtsEngine;
        switch (engine)
        {
            case ElevenLabsProvider:
                request.Headers.TryAddWithoutValidation("xi-api-key", key);
                break;
            case "azure":
                request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", key);
                break;
            case DeepgramProvider:
                request.Headers.Authorization = new AuthenticationHeaderValue("Token", key);
                break;
            default:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                break;
        }
    }

    /// <summary>
    /// Transcreve um arquivo de áudio no provider STT configurado.
    /// </summary>
    /// <param name="stream">Conteúdo do arquivo.</param>
    /// <param name="fileName">Nome original (define formato).</param>
    /// <param name="ct">Cancelamento.</param>
    /// <exception cref="AudioDisabledException">STT desabilitada.</exception>
    /// <exception cref="AudioProviderException">Erro do provider.</exception>
    public async Task<string> TranscribeAsync(
        Stream stream, string fileName, CancellationToken ct = default)
    {
        var cfg = await ResolveProvidersAsync(await GetConfigAsync(ct), ct);
        if (!cfg.SttEnabled)
        {
            throw new AudioDisabledException("STT desabilitada — configure um provider.");
        }

        var http = httpFactory.CreateClient(nameof(AudioService));
        // whisper externo: SPEC pede 60s; demais engines seguem em 120s.
        http.Timeout = TimeSpan.FromSeconds(cfg.SttEngine == "whisper" ? 60 : 120);
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        form.Add(new StringContent(cfg.SttModel ?? "whisper-1"), "model");

        var url = cfg.SttEngine == DeepgramProvider
            ? $"{cfg.SttBaseUrl!.TrimEnd('/')}/v1/listen?model={cfg.SttModel ?? "nova-2"}"
            : $"{cfg.SttBaseUrl!.TrimEnd('/')}/audio/transcriptions";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrEmpty(cfg.SttApiKey))
        {
            request.Headers.Authorization = cfg.SttEngine == DeepgramProvider
                ? new AuthenticationHeaderValue("Token", cfg.SttApiKey)
                : new AuthenticationHeaderValue("Bearer", cfg.SttApiKey);
        }
        request.Content = cfg.SttEngine == DeepgramProvider
            ? new StreamContent(stream)
            : form;
        if (cfg.SttEngine == DeepgramProvider)
        {
            request.Content.Headers.ContentType =
                new MediaTypeHeaderValue("application/octet-stream");
        }

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new AudioProviderException(
                $"Provider STT respondeu {(int)response.StatusCode}.");
        }

        var doc = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
        if (doc.TryGetProperty("text", out var text))
        {
            return text.GetString() ?? string.Empty;
        }
        // Deepgram: results.channels[0].alternatives[0].transcript
        if (doc.TryGetProperty("results", out var results)
            && results.TryGetProperty("channels", out var channels)
            && channels.GetArrayLength() > 0
            && channels[0].TryGetProperty("alternatives", out var alts)
            && alts.GetArrayLength() > 0)
        {
            return alts[0].GetProperty("transcript").GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    /// <summary>Lista vozes do provider TTS, no endpoint próprio de cada engine.</summary>
    /// <param name="ct">Cancelamento.</param>
    public async Task<JsonElement> GetVoicesAsync(CancellationToken ct = default)
    {
        var cfg = await ResolveProvidersAsync(await GetConfigAsync(ct), ct);
        if (!cfg.TtsEnabled)
        {
            throw new AudioDisabledException("TTS desabilitado — configure um provider.");
        }

        var http = httpFactory.CreateClient(nameof(AudioService));
        using var request = cfg.TtsEngine switch
        {
            ElevenLabsProvider => new HttpRequestMessage(HttpMethod.Get,
                $"{ElevenLabsBase(cfg)}/v1/voices"),
            "azure" => new HttpRequestMessage(HttpMethod.Get,
                $"{AzureBase(cfg)}/cognitiveservices/voices/list"),
            _ => new HttpRequestMessage(HttpMethod.Get,
                $"{cfg.TtsBaseUrl!.TrimEnd('/')}/audio/voices"),
        };
        AddAuthHeader(request, cfg, isStt: false);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
    }

    /// <summary>Lista modelos do provider (GET /models — OpenAI/whisper; /v1/models — ElevenLabs).</summary>
    /// <param name="ct">Cancelamento.</param>
    public async Task<JsonElement> GetModelsAsync(CancellationToken ct = default)
    {
        var cfg = await ResolveProvidersAsync(await GetConfigAsync(ct), ct);
        string? url = cfg.SttEnabled
            ? $"{cfg.SttBaseUrl!.TrimEnd('/')}/models"
            : cfg.TtsEngine switch
            {
                ElevenLabsProvider when cfg.TtsEnabled => $"{ElevenLabsBase(cfg)}/v1/models",
                "openai" or "transformers" when cfg.TtsEnabled =>
                    $"{cfg.TtsBaseUrl!.TrimEnd('/')}/models",
                _ => null,
            };
        if (url is null)
        {
            throw new AudioDisabledException("Áudio desabilitado — configure um provider.");
        }

        var http = httpFactory.CreateClient(nameof(AudioService));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuthHeader(request, cfg, isStt: cfg.SttEnabled);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
    }
}

/// <summary>Provider de áudio não configurado (endpoint → 501).</summary>
public class AudioDisabledException(string message) : Exception(message);

/// <summary>Falha no provider de áudio (endpoint → 502 com detail).</summary>
public class AudioProviderException(string message) : Exception(message);
