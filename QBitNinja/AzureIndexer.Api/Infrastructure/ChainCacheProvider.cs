using System.Globalization;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace AzureIndexer.Api.Infrastructure
{
    using System;
    using System.IO;
    using System.Threading;
    using NBitcoin;
    using Stratis.Bitcoin.Features.AzureIndexer;

    public class ChainCacheProvider
    {
        private readonly string cacheFilePath;
        private readonly ConcurrentChain chain;
        private readonly IndexerClient client;

        public ChainCacheProvider(IConfiguration configuration, ConcurrentChain chain, IndexerClient client)
        {
            this.cacheFilePath = configuration["LocalChain"];
            this.chain = chain;
            this.client = client;
        }

        public bool IsCacheAvailable =>
            File.Exists(this.cacheFilePath) &&
            DateTime.UtcNow.Subtract(File.GetLastWriteTimeUtc(this.cacheFilePath)).TotalHours < 24;

        public void BuildCache()
        {
            try
            {
                this.LoadCache();

                var changes = this.client.GetChainChangesUntilFork(this.chain.Tip, false);
                try
                {
                    changes.UpdateChain(this.chain);
                }
                catch (ArgumentException)
                {
                    // Happen when chain in table is corrupted
                    this.client.Configuration.GetChainTable().DeleteIfExistsAsync().GetAwaiter().GetResult();
                    for (var i = 0; i < 20; i++)
                    {
                        try
                        {
                            if (this.client.Configuration.GetChainTable().CreateIfNotExistsAsync().GetAwaiter()
                                .GetResult())
                            {
                                break;
                            }
                        }
                        catch
                        {
                            // ignored
                        }

                        Thread.Sleep(10000);
                    }

                    this.client.Configuration.CreateIndexer().IndexChain(this.chain);
                }

                this.SaveChainCache();
            }
            catch
            {
                // ignore
            }
        }

        private void LoadCache()
        {
            if (string.IsNullOrEmpty(this.cacheFilePath))
                return;

            try
            {
                var bytes = File.ReadAllBytes(this.cacheFilePath);
                this.chain.Load(bytes);
            }
            catch
            {
                // We don't care if it don't succeed
            }
        }

        private void SaveChainCache()
        {
            if (string.IsNullOrEmpty(this.cacheFilePath))
                return;

            try
            {
                var file = new FileInfo(this.cacheFilePath);
                if (!file.Exists || (DateTime.UtcNow - file.LastWriteTimeUtc) > TimeSpan.FromDays(1))
                {
                    using (var fs = File.Open(this.cacheFilePath, FileMode.Create))
                    {
                        this.chain.WriteTo(fs);
                    }
                }
            }
            catch
            {
                // ignored
            }
        }
    }

    public class ChainCacheCheckMiddleware
    {
        private readonly RequestDelegate next;
        private readonly ChainCacheProvider provider;

        public ChainCacheCheckMiddleware(RequestDelegate next, ChainCacheProvider provider)
        {
            this.next = next;
            this.provider = provider;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (this.provider.IsCacheAvailable == false)
            {
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                await context.Response.WriteAsync("Cache is being built.");
                return;
            }

            await this.next(context);
        }
    }
}