using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using DataAO;
using DefaultNamespace;
using LitJson;
using NetWork.DataElment;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Profiling;
using UnityEditor;
using UnityEditor.VersionControl;
using UnityEditorInternal;
using UnityEngine;
using Tools = DefaultNamespace.Tools;


internal enum ETM_Runstate
{
    Check = 0,
    Import = 1,
    Importing = 101,
    Load = 2,
    Loading=202,
    SwitchPage1 = 401,
    SwitchPage2 = 402,
    SwitchingP1 = 403,
    SwitchingP2 = 404,
    Unload = 5,
    NewSnap = 6,
    TakeSnapShot=7,
    WaitForTakingSnapShot=8,
    ConnetGame = 9,
}

internal class Server : MonoBehaviour
{
    private SocketServer _sos;
    private ETM_Runstate m_CurrentState = ETM_Runstate.Check;
    private int port = 6620;
    private Unity.MemoryProfiler.Editor.MemoryProfilerWindow mp_windows;
    private Queue<Reparam> _analyzeQue = new Queue<Reparam>(10);
    private ServerConfig Config;
    private Reparam Current_Snap;
    private float timmer;
    private bool isTakeSnapShotSuccess = false;
    private string memorysnapFileName = string.Empty;
    private UploadProcess up;
    private int retryCount = 0;
    private DataMes Message;
    private Action<string, bool, DebugScreenCapture> screenshotCaptureFunc = null;   //快照截图流程
    private void Awake()
    {
        //打开MemoryProfiler
        EditorApplication.ExecuteMenuItem("Window/Analysis/Memory Profiler");
        _sos = new SocketServer();
        StartSocketServer();
        _sos.mydelegate += ParseProfiler;
        //初始化Config配置文件
        string originString = File.ReadAllText("./Assets/ServerConfig.json");
        Config = JsonMapper.ToObject<ServerConfig>(originString);
        //初始化上传服务对象
        up = new UploadProcess(Config.MsgRobot.RobotUrl,Config.UploadAPI.ReleaseUrl);
        Current_Snap = new Reparam();
    }

    // Start is called before the first frame update
    void Start()
    {
        mp_windows =  EditorWindow.GetWindow<Unity.MemoryProfiler.Editor.MemoryProfilerWindow>();
        ThreadPool.SetMinThreads(3, 3);
        ThreadPool.SetMaxThreads(3, 3);
        Message = new DataMes();
        Message.ip = getLocalIPAddressWithNetworkInterface();
        Message.isConnected = false;
        //初始化截图流程
        screenshotCaptureFunc = (string path, bool result, DebugScreenCapture screenCapture) =>
        {
            if (screenCapture.RawImageDataReference.Length == 0)
                return;
                
            if (Path.HasExtension(path))
            {
                path = Path.ChangeExtension(path, ".png");
            }
                
            Texture2D tex = new Texture2D(screenCapture.Width, screenCapture.Height, screenCapture.ImageFormat, false);
            CopyDataToTexture(tex, screenCapture.RawImageDataReference);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(tex);
            else
                UnityEngine.Object.DestroyImmediate(tex);
        };
    }

    // Update is called once per frame
    void Update()
    {
        if (m_CurrentState == ETM_Runstate.Check)
        {
            Manager();
        }
        else if (m_CurrentState == ETM_Runstate.Import)
        {
            mp_windows.SnapshotDataService.Import(Current_Snap.LocalFilePath);
            m_CurrentState = ETM_Runstate.Importing;
        }

        else if (m_CurrentState == ETM_Runstate.Importing)
        {
            //等待2秒Import完毕
            timmer += Time.deltaTime;
            if (timmer>=2f)
            {
                m_CurrentState = ETM_Runstate.Load;
                timmer = 0f;
            }
        }

        else if (m_CurrentState == ETM_Runstate.Load)
        {
            mp_windows.SnapshotDataService.Load(Current_Snap.LocalFilePath);
            m_CurrentState = ETM_Runstate.Loading;
        }

        else if (m_CurrentState == ETM_Runstate.Loading)
        {
            if (mp_windows.SnapshotDataService.Base.Valid)
            {
                //Load完成，进行切换界面
                m_CurrentState = ETM_Runstate.SwitchPage1;
            }
        }

        else if (m_CurrentState == ETM_Runstate.SwitchPage1)
        {
            m_CurrentState = ETM_Runstate.SwitchingP1;
            mp_windows.ProfilerViewController.SetTabBarView(1);
        }
        
        else if (m_CurrentState == ETM_Runstate.SwitchingP1)
        {
            if (mp_windows.ProfilerViewController.IsViewLoadedComplte("unityObjects"))
            {
                m_CurrentState = ETM_Runstate.SwitchPage2;
                //等待加载完毕再进行
            }
        }
        
        else if (m_CurrentState == ETM_Runstate.SwitchPage2)
        {
            m_CurrentState = ETM_Runstate.SwitchingP2;
            mp_windows.ProfilerViewController.SetTabBarView(2);
        }

        else if (m_CurrentState == ETM_Runstate.SwitchingP2)
        {
            if (mp_windows.ProfilerViewController.IsViewLoadedComplte("allMemory"))
            {
                //等待加载完毕再进行
                GetParseData();
            }
        }
        
        else if (m_CurrentState == ETM_Runstate.Unload)
        {
            mp_windows.SnapshotDataService.UnloadAll();
            m_CurrentState = ETM_Runstate.Check;
        }
        
        else if (m_CurrentState == ETM_Runstate.NewSnap)
        {
            ParseQue();
        }
        
        else if(m_CurrentState == ETM_Runstate.WaitForTakingSnapShot)
        {
            CheckTakeSnapShot();
        }
        
        else if(m_CurrentState == ETM_Runstate.TakeSnapShot)
        {
            TakeSnapShot();
        }
        
        else if (m_CurrentState == ETM_Runstate.ConnetGame)
        {
            ConnectGame();
            m_CurrentState = ETM_Runstate.Check;
        }
    }

    /// <summary>
    /// 检查各个状态进行转换
    /// </summary>
    private void Manager()
    {
        if (ProfilerDriver.connectedProfiler == -1 && Message.isConnected)
        {
            //重试三次，超过三次还连不上的话设为断开连接
            if(retryCount > 2)
            {
                if (ProfilerDriver.connectedProfiler == -1)
                {
                    UnityEngine.Debug.Log("Profiler连接了三次游戏仍然未连接成功，请检查连接的IP为："+Message.ip);
                    Tools.SendMessage(Config.MsgRobot.RobotUrl,"<at email=\"chenderui1@thewesthill.net\">陈德睿</at>## Profiler连接了三次游戏仍然未连接成功\n\n> 请检查：" + Message.ip);
                    Message.isConnected = false;
                    retryCount = 0;
                }
            }
            else
            {
                //由于某种原因退出了连接，再次重新连接
                UnityEngine.Debug.Log("重连第"+retryCount+"次");
                ConnectGame();
                retryCount += 1;
            }
        }
        if (_analyzeQue.Count!=0)
        {
            m_CurrentState = ETM_Runstate.NewSnap;
        }
    }
    
    
    #region 连接游戏
    private void ConnectGame()
    {
        ProfilerDriver.DirectIPConnect(Message.ip);
        Message.isConnected = true;
    }
    #endregion
    
    #region 检查是否成功TakeSnapShot
    private void CheckTakeSnapShot()
    {
        if (isTakeSnapShotSuccess)
        {
            //截取完成了
            isTakeSnapShotSuccess = !isTakeSnapShotSuccess;
            m_CurrentState = ETM_Runstate.Import;
        }
    }
    #endregion
    
    /// <summary>
    /// 处理数据，将数据捞出来并进行上传操作
    /// </summary>
    void GetParseData()
    {
        Debug.Log("进入了处理数据流程");
        try
        {
            //Tools.SendMessage(Config.MsgRobot.RobotUrl,"Enter analyzeing process.");
            Debug.Log($"开始上传文件:{Current_Snap.FileName}");
            var summaryJson = mp_windows.ProfilerViewController.GetMemoryData("summary");
            var unityObjectsJson = mp_windows.ProfilerViewController.GetMemoryData("unityObjects");
            var allMemoryJson = mp_windows.ProfilerViewController.GetMemoryData("allMemory");
            up.WriteResultFile($"{Path.GetDirectoryName(Current_Snap.LocalFilePath)}/{Current_Snap.Uuid}_SummaryData.json",summaryJson);
            up.WriteResultFile($"{Path.GetDirectoryName(Current_Snap.LocalFilePath)}/{Current_Snap.Uuid}_UnityObjects.json",unityObjectsJson);
            up.WriteResultFile($"{Path.GetDirectoryName(Current_Snap.LocalFilePath)}/{Current_Snap.Uuid}_AllMemoryData.json",allMemoryJson);
            up.UploadCompressedFile(Current_Snap);
            m_CurrentState = ETM_Runstate.Unload;
        }
        catch (Exception e)
        {
            Debug.LogError(e.ToString());
            m_CurrentState = ETM_Runstate.Check;
        }
    }

    /// <summary>
    /// 对已经下载解压缩的队列中数据进行操作
    /// </summary>
    void ParseQue()
    {
        if (_analyzeQue.Count != 0 && _analyzeQue.TryDequeue(out Reparam anaData))
        {
            try
            {
                if (!anaData.Tag.Equals("auto"))
                {
                    Current_Snap.Appkey = anaData.Appkey;
                    Current_Snap.Uuid = anaData.Uuid;
                    Current_Snap.Model = anaData.Model;
                    Current_Snap.SceneName = anaData.SceneName;
                    Current_Snap.CaseName = anaData.CaseName;
                    Current_Snap.Tag = anaData.Tag;
                    Current_Snap.FileName = anaData.FileName;
                    Current_Snap.BuildTime = anaData.BuildTime;
                    Current_Snap.Version = anaData.Version;
                    Current_Snap.Channel = anaData.Channel;
                    Current_Snap.Branch = anaData.Branch;
                    Current_Snap.LocalFilePath = anaData.LocalFilePath;
                    memorysnapFileName = Current_Snap.LocalFilePath;
                    m_CurrentState = ETM_Runstate.TakeSnapShot;
                }
                else
                {
                    Current_Snap.Appkey = anaData.Appkey;
                    Current_Snap.Uuid = anaData.Uuid;
                    Current_Snap.Model = anaData.Model;
                    Current_Snap.SceneName = anaData.SceneName;
                    Current_Snap.CaseName = anaData.CaseName;
                    Current_Snap.Tag = anaData.Tag;
                    Current_Snap.FileName = anaData.FileName;
                    Current_Snap.BuildTime = anaData.BuildTime;
                    Current_Snap.Version = anaData.Version;
                    Current_Snap.Channel = anaData.Channel;
                    Current_Snap.Branch = anaData.Branch;
                    Current_Snap.LocalFilePath = anaData.LocalFilePath;
                    m_CurrentState = ETM_Runstate.Import;
                }
            }
            catch (Exception e)
            {
                Debug.LogError(e);
            }
        }
    }

    /// <summary>
    /// 处理接受到的消息
    /// </summary>
    /// <param name="msg">Socket传入的消息</param>
    /// <returns></returns>
    ResponseData ParseProfiler(string msg)
    {
        Debug.Log($"Receive Data：{msg}");
        ResponseData result;
        List<int> reportData = new List<int>();
        if (!string.IsNullOrEmpty(msg))
        {
            //定义各种数据传输形式
            if (msg.Contains("Appkey") && !msg.Contains("handle"))
            {
                try
                {
                    Debug.Log($"Input param：{msg}");
                    char[] charsToTrim = { '"', ' ' };
                    Reparam resData = JsonMapper.ToObject<Reparam>(msg.Trim(charsToTrim));
                    _analyzeQue.Enqueue(resData);
                    result = new ResponseData(200,"Receive AnalyzeDataSuccess.",false,reportData);
                }
                catch (Exception e)
                {
                    Debug.LogError(e);
                    result = new ResponseData(500, e.ToString(),false,reportData);
                }
            }
            else if (msg.Contains("Appkey") && msg.Contains("handle"))
            {
                try
                {
                    Debug.Log($"Input param：{msg}");
                    Reparam resData = JsonMapper.ToObject<Reparam>(msg);
                    _analyzeQue.Enqueue(resData);
                    result = new ResponseData(200,"Receive AnalyzeDataSuccess.",false,reportData);
                }
                catch (Exception e)
                {
                    Debug.LogError(e);
                    result = new ResponseData(500, e.ToString(),false,reportData);
                }
            }
            else if (msg.Contains("connect"))  //{"connect":1,"ip":"10.11.144.31","platform":"PC or PS5","branch":"主干/分支"}
            {
                try
                {
                    Debug.Log($"Input param：{msg}");
                    var getdata = JsonMapper.ToObject<ConnectData>(msg);
                    if(!msg.Contains("local"))
                        Message.ip = getdata.ip;
                    Message.isConnected = false;
                    m_CurrentState = ETM_Runstate.ConnetGame;
                    result = new ResponseData(200, "Receive connect message success.", false,reportData);
                }
                catch (Exception e)
                {
                    Debug.LogError(e);
                    result = new ResponseData(500, e.ToString(),false,reportData);
                }
            }
            else if (msg.Contains("TaskState"))  // {"TaskState":1,}
            {
                try
                {
                    if (_analyzeQue.Count ==0 && m_CurrentState == ETM_Runstate.Check && up.IsUploadAllfile)
                    {
                        reportData.AddRange(up.all_Url);
                        result = new ResponseData(200,"All file is success.",true,reportData);
                    }
                    else
                    {
                        result = new ResponseData(200, "Current has some file is uploading.", false,reportData);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError(e);
                    result = new ResponseData(500, e.ToString(),false,reportData);
                }
            }
            else  //other state
            {
                Debug.Log("暂不支持的类型解析操作");
                result = new ResponseData(404, "This param is not supported now.",false,reportData);
            }
        }
        else
        {
            result = new ResponseData(400, "Message is null.",false,reportData);
        }
        return result;
    }
    
    
    /// <summary>
    /// 开始监听端口
    /// </summary>
    void StartSocketServer()
    {
        for (int i = 0; i < 5; ++i)
        {
            bool isInused = IsPortInUse(port + i);
            if (isInused)
            {
                Debug.Log($"This port {port+i} is in used");
                continue;
            }
            else
            {
                _sos.start(port+i);
                Debug.Log($"Tcp Listen in port：{port+i}");
                break;
            }
        }
    }
    
    /// <summary>
    /// IsPortInUse
    /// </summary>
    /// <param name="port"></param>
    /// <returns></returns>
    static bool IsPortInUse(int port)
    {
        bool isPortInUse = false;

        IPGlobalProperties ipGlobalProperties = IPGlobalProperties.GetIPGlobalProperties();
        IPEndPoint[] activeTcpListeners = ipGlobalProperties.GetActiveTcpListeners();
        IPEndPoint[] activeUdpListeners = ipGlobalProperties.GetActiveUdpListeners();

        foreach (IPEndPoint endPoint in activeTcpListeners)
        {
            if (endPoint.Port == port)
            {
                isPortInUse = true;
                break;
            }
        }

        if (!isPortInUse)
        {
            foreach (IPEndPoint endPoint in activeUdpListeners)
            {
                if (endPoint.Port == port)
                {
                    isPortInUse = true;
                    break;
                }
            }
        }
        return isPortInUse;
    }

    /// <summary>
    /// 清理磁盘操作
    /// </summary>
    void DetectDiskSize(string filesysPath)
    {
        string[] files = Directory.GetFiles(filesysPath, "*.*", SearchOption.AllDirectories);
        foreach (var item in files)  //5天一删
        {
            if (item.EndsWith("gz"))
            {
                FileInfo f = new FileInfo(item);
                DateTime nowtime = DateTime.Now;
                if (GetUnixTimeStamp(f.LastWriteTime)-GetUnixTimeStamp(nowtime) > 432000)
                {
                    File.Delete(item);
                }
            }
            else if (item.EndsWith("snap"))
            {
                FileInfo f = new FileInfo(item);
                DateTime nowtime = DateTime.Now;
                if (GetUnixTimeStamp(f.LastWriteTime)-GetUnixTimeStamp(nowtime) > 432000)
                {
                    File.Delete(item);
                }
            }
        }
    }
    
    /// <summary>
    /// 内存快照回调
    /// </summary>
    /// <param name="str">返回结果内容</param>
    /// <param name="bl">返回结果是否成功</param>
    private void MemorySnapshotCallback(string str, bool bl)
    {
        if (bl)
        {
            isTakeSnapShotSuccess = true;
            UnityEngine.Debug.Log("截取快照完成：" + memorysnapFileName);
            Tools.SendMessage(Config.MsgRobot.RobotUrl,"截取MemoryProfiler快照成功CaseName:"+Current_Snap.CaseName);
        }
        else
            isTakeSnapShotSuccess = true;
    }
    
    void CopyDataToTexture(Texture2D tex, NativeArray<byte> byteArray)
    {
        unsafe
        {
            void* srcPtr = NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(byteArray);
            void* dstPtr = tex.GetRawTextureData<byte>().GetUnsafeReadOnlyPtr();
            UnsafeUtility.MemCpy(dstPtr, srcPtr, byteArray.Length * sizeof(byte));
        }
    }
    
    private void TakeSnapShot()
    {
        string dir = Path.GetDirectoryName(memorysnapFileName);
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
#if UNITY_2022_3_OR_NEWER
        Unity.Profiling.Memory.MemoryProfiler.TakeSnapshot(memorysnapFileName, MemorySnapshotCallback,screenshotCaptureFunc, Unity.Profiling.Memory.CaptureFlags.ManagedObjects | Unity.Profiling.Memory.CaptureFlags.NativeObjects | Unity.Profiling.Memory.CaptureFlags.NativeAllocations | Unity.Profiling.Memory.CaptureFlags.NativeAllocationSites | Unity.Profiling.Memory.CaptureFlags.NativeStackTraces);
#elif UNITY_2021_1_OR_NEWER
        MemoryProfiler.TakeSnapshot(memorysnapFileName, MemorySnapshotCallback, screenshotCaptureFunc,CaptureFlags.ManagedObjects | CaptureFlags.NativeObjects | CaptureFlags.NativeAllocations | CaptureFlags.NativeAllocationSites | CaptureFlags.NativeStackTraces);
#else
        UnityEditor.MemoryProfiler.TakeSnapshot(memorysnapFileName, MemorySnapshotCallback, screenshotCaptureFunc, CaptureFlags.ManagedObjects | CaptureFlags.NativeObjects | CaptureFlags.NativeAllocations | CaptureFlags.NativeAllocationSites | CaptureFlags.NativeStackTraces);
#endif
        m_CurrentState = ETM_Runstate.WaitForTakingSnapShot;
    }
    
    /// <summary>
    /// 转换时间戳
    /// </summary>
    /// <param name="dt"></param>
    /// <returns></returns>
    public static long GetUnixTimeStamp(DateTime dt)
    {
        DateTime dtStart = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(1970, 1, 1, 0, 0, 0), TimeZoneInfo.Local);
        long timeStamp = Convert.ToInt32((dt - dtStart).TotalSeconds);
        return timeStamp;
    }
    #region 获取IP
    public string getLocalIPAddressWithNetworkInterface()
    {
        string output = "";
        foreach (NetworkInterface item in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (item.OperationalStatus == OperationalStatus.Up && item.GetIPProperties().UnicastAddresses.Count > 0)
            {
                //UnityEngine.Debug.Log("Interface: " + item.Name);
                // 获取接口的IP地址信息
                IPInterfaceProperties ipProperties = item.GetIPProperties();
                UnicastIPAddressInformationCollection ipAddresses = ipProperties.UnicastAddresses;

                foreach (UnicastIPAddressInformation ipAddress in ipAddresses)
                {
                    // 只获取IPv4地址
                    if (ipAddress.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && ipAddress.Address.ToString().Contains("10.11"))
                    {
                        output = ipAddress.Address.ToString();
                        break;
                    }
                }
            }
            if (!string.IsNullOrEmpty(output))
            {
                break;
            }
        }
        return output;
    }
    #endregion
}
