using System.Net;
using System.Text;
using System.Text.Json;
using XBank.CodeGraph.Retrieval;
using XBank.CodeGraph.Storage;

namespace XBank.CodeGraph.Serving;

public static class HttpServer
{
    public static async Task RunAsync(string db, int port)
    {
        // فقط روی localhost: این سرور احتیاجی به احراز هویت ندارد چون
        // بیرون از ماشین قابل دسترسی نیست. اگر prefix را به + یا 0.0.0.0
        // تغییر دادی، کد منبعت را بدون احراز هویت روی شبکه باز کرده‌ای.
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Console.WriteLine($"در حال گوش دادن روی http://127.0.0.1:{port}/context?q=...");

        using var store = new GraphStore(db);
        var retriever = new ContextRetriever(store, new RetrieveOptions());

        while (true)
        {
            var ctx = await listener.GetContextAsync();
            _ = Task.Run(() =>
            {
                try
                {
                    var q = ctx.Request.QueryString["q"] ?? "";
                    var body = ctx.Request.Url?.AbsolutePath switch
                    {
                        "/context" => JsonSerializer.Serialize(new
                        {
                            content = retriever.BuildContext(q)
                        }),
                        "/search" => JsonSerializer.Serialize(store.Search(q, 20)),
                        _ => "{\"error\":\"not found\"}"
                    };
                    var bytes = Encoding.UTF8.GetBytes(body);
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    ctx.Response.OutputStream.Write(bytes);
                }
                catch (Exception ex)
                {
                    ctx.Response.StatusCode = 500;
                    Console.Error.WriteLine(ex.Message);
                }
                finally { ctx.Response.Close(); }
            });
        }
    }
}
