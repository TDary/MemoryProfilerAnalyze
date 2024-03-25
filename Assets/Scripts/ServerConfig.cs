namespace DefaultNamespace
{
    public class ServerConfig
    {
        public string LocalFilePath { get; set; }
        public MsgRobot MsgRobot { get; set; }
        public UploadAPI UploadAPI { get; set; }
    }

    public class MsgRobot
    {
        public string RobotUrl { get; set; }
    }

    public class UploadAPI
    {
        public string DebugUrl { get; set; }
        public string ReleaseUrl { get; set; }
    }
}