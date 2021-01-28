using Microsoft.AspNetCore.Mvc.Rendering;
using System.Collections.Generic;
using System.ComponentModel;

namespace Portal.Models
{
    public class HomeViewModel
    {
        [DisplayName("Select Device")]
        public string deviceId { get; set; }
        public IEnumerable<SelectListItem> deviceList { get; set; }
        public string newDeviceId { get; set; }
        public string registrationId { get; set; }
        public IEnumerable<SelectListItem> enrollmentList { get; set; }
    }
}
