using System;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Stomme.Modules.Pipelines.Controllers
{
    /// <summary>
    /// For an action that reads the request body ITSELF (a webhook it verifies the signature of, a call, an event, a form it bounds
    /// before reading): MVC's form value providers are removed, so binding the action's parameters never reads a URL-encoded or
    /// multipart body first. Without it, a correctly signed URL-encoded webhook call was refused 401 - the
    /// form value provider had consumed the body, so the signature was checked over nothing - and a form field called <c>name</c>
    /// was bound in place of the route's <c>{name}</c> (form values are consulted before route values). The shape is the one the
    /// ASP.NET Core documentation gives for streaming uploads.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class DisableFormValueModelBindingAttribute : Attribute, IResourceFilter
    {
        public void OnResourceExecuting(ResourceExecutingContext context)
        {
            context.ValueProviderFactories.RemoveType<FormValueProviderFactory>();
            context.ValueProviderFactories.RemoveType<FormFileValueProviderFactory>();
            context.ValueProviderFactories.RemoveType<JQueryFormValueProviderFactory>();
        }

        public void OnResourceExecuted(ResourceExecutedContext context)
        {
        }
    }
}
