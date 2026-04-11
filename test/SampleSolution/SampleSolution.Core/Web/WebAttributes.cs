namespace Microsoft.AspNetCore.Mvc;

[AttributeUsage(AttributeTargets.Method)]
public class HttpGetAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method)]
public class HttpPostAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method)]
public class HttpPutAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method)]
public class HttpDeleteAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method)]
public class HttpPatchAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class AuthorizeAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method)]
public class AllowAnonymousAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method)]
public class ValidateAntiForgeryTokenAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Class)]
public class AutoValidateAntiforgeryTokenAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class IgnoreAntiforgeryTokenAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Class)]
public class ApiControllerAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RouteAttribute : Attribute
{
    public RouteAttribute(string template) { }
}

public class Controller { }

public class ControllerBase { }
