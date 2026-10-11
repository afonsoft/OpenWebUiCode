using System.Text.Json;
using Microsoft.JSInterop;

namespace OpenWebUI.Client.Services;

/// <summary>
/// Falhas transitórias de transporte/parse que a UI deve tratar como
/// "offline/degradado" — nunca devem subir até a blazor-error-ui:
/// queda de rede ou proxy (<see cref="HttpRequestException"/>), timeout ou
/// abort do fetch (<see cref="OperationCanceledException"/> — inclui
/// <see cref="TaskCanceledException"/> do timeout do HttpClient), interop JS
/// indisponível (<see cref="JSException"/>) e resposta não-JSON de
/// proxy/CDN (<see cref="JsonException"/>, <see cref="NotSupportedException"/>).
/// Exceções fora dessa lista (bugs de lógica) continuam propagando — o erro
/// real precisa aparecer para ser corrigido.
/// </summary>
public static class ClientErrors
{
    /// <summary>True se a exceção é transitória de rede/parse do cliente.</summary>
    public static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException => true,
        OperationCanceledException => true,
        JSException => true,
        JsonException => true,
        NotSupportedException => true,
        _ => false,
    };
}
