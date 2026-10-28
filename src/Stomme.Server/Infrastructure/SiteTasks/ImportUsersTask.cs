using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Stomme.Managers;
using Stomme.Models;
using Stomme.Repository;

namespace Stomme.Infrastructure
{
    public class ImportUsersTask : SiteTaskBase
    {
        public override async Task<string> ExecuteTaskAsync(IServiceProvider provider, Site site, string parameters)
        {
            string log = "";

            if (!string.IsNullOrEmpty(parameters) && parameters.Contains(":"))
            {
                var fileId = int.Parse(parameters.Split(':')[0]);
                var notify = bool.Parse(parameters.Split(':')[1]);

                var fileRepository = provider.GetRequiredService<IFileRepository>();
                var userManager = provider.GetRequiredService<IUserManager>();

                var file = await fileRepository.GetFileAsync(fileId);
                if (file != null)
                {
                    var filePath = await fileRepository.GetFilePathAsync(file);
                    log += $"Importing Users From {filePath}<br />";
                    var result = await userManager.ImportUsers(site.SiteId, filePath, notify);
                    if (result["Success"] == "True")
                    {
                        log += $"{result["Users"]} Users Imported<br />";
                    }
                    else
                    {
                        log += $"User Import Failed<br />";
                    }
                }
                else
                {
                    log += $"Import Users FileId {fileId} Does Not Exist<br />";
                }
            }

            return log;
        }
    }
}
