using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Wonder.Host;

public sealed class RequestValidationFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is null) continue;
            var type = typeof(IValidator<>).MakeGenericType(argument.GetType());
            foreach (var validator in context.HttpContext.RequestServices.GetServices(type).OfType<IValidator>())
            {
                var result = await validator.ValidateAsync(new ValidationContext<object>(argument),
                    context.HttpContext.RequestAborted);
                foreach (var error in result.Errors)
                    context.ModelState.AddModelError(error.PropertyName, error.ErrorMessage);
            }
        }

        if (!context.ModelState.IsValid)
        {
            context.Result = new BadRequestObjectResult(new ValidationProblemDetails(context.ModelState)
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid request"
            });
            return;
        }
        await next();
    }
}
