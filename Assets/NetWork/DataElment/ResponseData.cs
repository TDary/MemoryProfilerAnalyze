using System.Collections.Generic;

namespace NetWork.DataElment
{
    public class ResponseData
    {
        public int Code { get;set; }
        public string Msg { get;set; }
        public bool AnalyzeState { get; set; }
        public List<int> ReportIdList { get;set; }
        public ResponseData(int code,string msg,bool state,List<int> data)
        {
            this.Code = code;
            this.Msg = msg;
            this.AnalyzeState = state;
            this.ReportIdList = new List<int>();
            this.ReportIdList.AddRange(data);
        }
    }
}