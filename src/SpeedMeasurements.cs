using System;
using System.IO;
using System.Net.Http;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace CleanBrowser {
    public static class SpeedMeasurements {
        public const int MaxBytes=10000000;
        public static double Average(long bytes,double seconds){if(bytes<=0||seconds<=0||Double.IsNaN(seconds)||Double.IsInfinity(seconds))throw new ArgumentOutOfRangeException();return bytes*8.0/seconds/1000000;}
        public static async Task<double> Download(HttpClient client,string url,Action<double> progress,CancellationToken ct,int durationMs=4000){
            using(var budget=CancellationTokenSource.CreateLinkedTokenSource(ct)){
                budget.CancelAfter(15000);
                using(var response=await client.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,budget.Token)){
                    response.EnsureSuccessStatusCode();using(var stream=await response.Content.ReadAsStreamAsync()){
                        byte[] buffer=new byte[65536];long total=0,last=0;var clock=Stopwatch.StartNew();budget.CancelAfter(durationMs);
                        try{while(total<MaxBytes){int read=await stream.ReadAsync(buffer,0,(int)Math.Min(buffer.Length,MaxBytes-total),budget.Token);if(read==0)break;total+=read;if(clock.ElapsedMilliseconds-last>=200){last=clock.ElapsedMilliseconds;if(progress!=null)progress(Average(total,Math.Max(clock.Elapsed.TotalSeconds,.001)));}}}
                        catch(OperationCanceledException){ct.ThrowIfCancellationRequested();}
                        ct.ThrowIfCancellationRequested();if(total==0)throw new IOException("未收到下载数据");double result=Average(total,Math.Max(clock.Elapsed.TotalSeconds,.001));if(progress!=null)progress(result);return result;
                    }
                }
            }
        }
        public static async Task<double> Upload(HttpClient client,string url,Action<double> progress,CancellationToken ct,int durationMs=4000){
            byte[] buffer=new byte[1000000];using(var random=RandomNumberGenerator.Create())random.GetBytes(buffer);
            long total=0;double seconds=0;int chunk=65536;var overall=Stopwatch.StartNew();
            using(var budget=CancellationTokenSource.CreateLinkedTokenSource(ct)){
                budget.CancelAfter(durationMs);
                try{while(total<MaxBytes&&overall.ElapsedMilliseconds<durationMs){
                    int size=(int)Math.Min(chunk,MaxBytes-total);var sample=Stopwatch.StartNew();
                    using(var payload=new ByteArrayContent(buffer,0,size)){
                        payload.Headers.ContentType=new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                        using(var response=await client.PostAsync(url,payload,budget.Token)){response.EnsureSuccessStatusCode();}
                    }
                    // Only count bytes acknowledged by a successful server response.
                    total+=size;seconds+=Math.Max(sample.Elapsed.TotalSeconds,.001);double average=Average(total,seconds);if(progress!=null)progress(average);
                    chunk=(int)Math.Max(65536,Math.Min(buffer.Length,average*1000000/8*.35));
                }}catch(OperationCanceledException){ct.ThrowIfCancellationRequested();}
                ct.ThrowIfCancellationRequested();if(total==0)throw new IOException("上传未完成有效样本，请重试");return Average(total,seconds);
            }
        }
    }
}
