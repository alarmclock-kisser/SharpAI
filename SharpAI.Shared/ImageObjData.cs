using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace SharpAI.Shared
{
	public class ImageObjData
	{
		public Guid Id { get; set; }
		public string Data { get; set; } = string.Empty;
		public string MimeType { get; set; } = string.Empty;




		[JsonConstructor]
		public ImageObjData()
		{

		}


		public ImageObjData(Guid id, string data, string mimeType = "image/png")
		{
			this.Id = id;
			this.Data = data;
			this.MimeType = mimeType;
		}
	}
}
