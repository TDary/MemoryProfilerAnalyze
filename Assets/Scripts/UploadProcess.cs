using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DataAO;
using LitJson;
using NetWork.DataElment;
using UnityEngine;

namespace DefaultNamespace
{
    public class UploadProcess
    {
        //测试环境：http://10.11.176.233:5567
        //正式环境：http://10.11.67.163:5566
        private string requestUploadurl = string.Empty;
        private string requestAnalyzeUrl = string.Empty;
        private string robotUrl = string.Empty;
        public bool IsUploadAllfile = true;
        public List<int> all_Url = new List<int>();
        public UploadProcess(string inputUrl,string uploadUrl)
        {
            if (!string.IsNullOrEmpty(inputUrl) && !string.IsNullOrEmpty(uploadUrl))
            {
                this.robotUrl = inputUrl;
                this.requestUploadurl = $"{uploadUrl}api/v2/report/upload/url";
                this.requestAnalyzeUrl = $"{uploadUrl}api/v2/report/analysis";
            }
            else
            {
                Debug.LogError("input url is null or empty.");
            }
        }
        
        public void WriteResultFile(string filename,string data)
        {
            File.WriteAllText(filename,data);
        }

        /// <summary>
        /// 上传压缩了的文件
        /// </summary>
        public void UploadCompressedFile(Reparam aitem)
        {
            IsUploadAllfile = false;
            try
            {
                string originalFilePath = Path.GetDirectoryName(aitem.LocalFilePath);
                string desFilePath = originalFilePath + ".zip";
                //压缩文件进行上传操作
                ZipFile.CreateFromDirectory(originalFilePath,desFilePath);
                string buildtime = aitem.BuildTime;
                if (string.IsNullOrEmpty(buildtime))
                {
                    buildtime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                }
                var scenename = UnicodeDecode(aitem.SceneName);
                var modelname = UnicodeDecode(aitem.Model);
                var branchname = UnicodeDecode(aitem.Branch);
                var casename = UnicodeDecode(aitem.CaseName);
                RequestParam paramObj = new RequestParam
                {
                    projectId=aitem.Appkey,
                    uuid=aitem.Uuid,
                    model=modelname,
                    sceneName=scenename,
                    tag=aitem.Tag,
                    fileName=aitem.FileName.Split('.')[0]+".zip",
                    version=aitem.Version,
                    branch=branchname,
                    channel=aitem.Channel,
                    buildTime=buildtime, //2024-03-04 10:11:11
                    caseName=casename
                };
                var infojson = JsonMapper.ToJson(paramObj);
                string res = HttpRequestPost(requestUploadurl,infojson);
                Debug.Log($"请求参数：{infojson}");
                if (!string.IsNullOrEmpty(res))
                {
                    Debug.Log($"请求成功：{res}");
                    ReceiveData RevData = new ReceiveData();
                    RevData.data = new Data();
                    RevData = JsonMapper.ToObject<ReceiveData>(res);
                    if (RevData.msg.Contains("success"))
                    {
                        string uploadUrl = RevData.data.upload_url;
                        int id = RevData.data.report_id;
                        string analyze = $"{requestAnalyzeUrl}?reportId={id}";
                        string ResContent = HttpUploadFile(uploadUrl, desFilePath);
                        if (string.IsNullOrEmpty(ResContent))
                        {
                            Debug.Log($"上传文件压缩包成功ID:{id}");
                            string analyzeRes = HttpRequestGet(analyze);
                            if (analyzeRes.Contains("分析成功"))
                            {
                                Debug.Log($"{analyzeRes}——ID：{id}");
                                File.Delete(desFilePath);
                                Directory.Delete(originalFilePath,true);
                                Debug.Log("Upload all file success.");
                                Tools.SendMessage(robotUrl,$"{paramObj.caseName}—ID:{id}-上传文件且请求解析完毕.");
                                //上传完毕后
                                IsUploadAllfile = true;
                                //完成一个的话all_Url.Add一个
                                all_Url.Add(id);
                            }
                            else
                            {
                                Debug.LogError($"请求解析失败-ID：{id}");
                                Tools.SendMessage(robotUrl,$"<at email=\"chenderui1@thewesthill.net\">陈德睿</at>{paramObj.fileName}—ID:{id}-请求解析失败--ErrorMsg:{analyzeRes}");
                                IsUploadAllfile = true;
                            }
                        }
                        else
                        {
                            Debug.LogError($"上传压缩包文件失败ID：{id}");
                            Tools.SendMessage( robotUrl,$"<at email=\"chenderui1@thewesthill.net\">陈德睿</at>{paramObj.fileName}上传源文件失败,请检查{desFilePath}--ErrorMsg:{ResContent}");
                            IsUploadAllfile = true;
                        }
                    }
                    else
                    {
                        Debug.LogError("失败的请求,请检查传入参数----");
                        Tools.SendMessage(robotUrl,$"<at email=\"chenderui1@thewesthill.net\">陈德睿</at>失败的请求,请检查传入参数{infojson}--ErrorMsg:{res}");
                        IsUploadAllfile = true;
                    }
                }
                else
                {
                    Debug.LogError($"请求上传接口失败：{res}");
                    Tools.SendMessage(robotUrl,$"<at email=\"chenderui1@thewesthill.net\">陈德睿</at>请求上传memoryProfiler接口失败:{aitem.FileName}--ErrorMsg:{res}");
                    IsUploadAllfile = true;
                }
            }
            catch (Exception e)
            {
                Debug.LogError(e);
                IsUploadAllfile = true;
            }
            string CapturesfilePath = string.Format("{0}\\{1}", System.Environment.CurrentDirectory, "MemoryCaptures");
            string[] files = Directory.GetFiles(CapturesfilePath);
            foreach (var item in files)// 清空目录文件夹下的所有文件
            {
                File.Delete(item);
            }
            System.GC.Collect();  //上传完GC一下
        }
        //Unicode解码
        private string UnicodeDecode(string unicodeStr)
        {
            if (string.IsNullOrWhiteSpace(unicodeStr) || (!unicodeStr.Contains("\\u") && !unicodeStr.Contains("\\U")))
            {
                return unicodeStr;
            }

            string newStr = Regex.Replace(unicodeStr, @"\\[uU](.{4})", (m) =>
            {
                string unicode = m.Groups[1].Value;
                if (int.TryParse(unicode, System.Globalization.NumberStyles.HexNumber, null, out int temp))
                {
                    return ((char)temp).ToString();
                }
                else
                {
                    return m.Groups[0].Value;
                }
            }, RegexOptions.Singleline);

            return newStr;
        }
        private string HttpRequestGet(string url)
        {
            try
            {
                // 创建 WebRequest 对象
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Timeout = 600000;
                // 设置请求方法为 GET
                request.Method = "GET";

                // 获取响应
                HttpWebResponse response = (HttpWebResponse)request.GetResponse();
                string responseBody = "";
                // 读取响应内容
                using (StreamReader streamReader = new StreamReader(response.GetResponseStream()))
                {
                    responseBody = streamReader.ReadToEnd();
                    Debug.Log(responseBody);
                }
                // 关闭响应
                response.Close();
                return responseBody;
            }
            catch (Exception ex)
            {
                Debug.LogError("发生异常: " + ex.Message);
                return null;
            }
        }
        private string HttpRequestPost(string url, string infojson)
        {
            //设置参数
            HttpWebRequest req = WebRequest.Create(url) as HttpWebRequest;
            req.Method = "POST";
            req.Timeout = 600000;
            //内容类型,设置为json
            req.ContentType = "application/json";
            req.KeepAlive = false;
            //设置参数，并进行url编码
            string paraUrlCoded = infojson;

            byte[] paload;
            //将json转换为字节流
            paload = System.Text.Encoding.UTF8.GetBytes(paraUrlCoded);
            //设置请求的ContentLength
            req.ContentLength = paload.Length;
            //发送请求，获取请求流
            Stream write;
            try
            {
                write = req.GetRequestStream();   //获取用于写入数据的Stream对象
            }
            catch (Exception e)
            {
                write = null;
                Debug.Log("Error——" + infojson + e.Message);
                return null;
            }
            //将请求参数写入流
            write.Write(paload, 0, paload.Length);
            write.Close();//关闭请求流

            HttpWebResponse resp;
            try
            {
                //获取响应流
                resp = (HttpWebResponse)req.GetResponse();
            }
            catch (WebException e)
            {
                resp = e.Response as HttpWebResponse;
                Debug.Log("Error——" + infojson + e.Message);
                return null;
            }

            Stream st = resp.GetResponseStream();
            StreamReader sRead = new StreamReader(st);
            string resContent = sRead.ReadToEnd();
            sRead.Close();
            req.Abort();
            resp.Close();
            return resContent;
        }
        private string HttpUploadFile(string url, string path)
        {
            //设置参数
            HttpWebRequest request = WebRequest.Create(url) as HttpWebRequest;
            CookieContainer cookiect = new CookieContainer();
            request.CookieContainer = cookiect;
            request.Method = "PUT";
            request.AllowAutoRedirect = true;
            request.Timeout = 600000;
            request.KeepAlive = false;
            FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            byte[] bArr = new byte[fs.Length];
            fs.Read(bArr, 0, bArr.Length);
            fs.Close();
            try
            {
                Stream postStream = request.GetRequestStream();
                postStream.Write(bArr, 0, bArr.Length);
                postStream.Close();
            }
            catch (Exception e)
            {
                Debug.Log("Error——" + path + e.Message);
                return "Error";
            }
            try
            {
                //发送请求并获取相应回应数据
                HttpWebResponse response = request.GetResponse() as HttpWebResponse;
                //直到request.GetResponse()程序才开始向目标网页发送Post请求
                Stream instream = response.GetResponseStream();
                StreamReader sr = new StreamReader(instream, Encoding.UTF8);
                //返回结果网页(html)代码
                string content = sr.ReadToEnd();
                request.Abort();
                response.Close();
                return content;
            }
            catch (Exception e)
            {
                Debug.Log("Error——" + path + e.Message);
                return  e.Message;
            }
        }
    }
}