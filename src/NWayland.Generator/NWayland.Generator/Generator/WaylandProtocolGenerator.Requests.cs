using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using Parameter = System.Reflection.Metadata.Parameter;

namespace NWayland.Generator
{
    partial class WaylandProtocolGenerator
    {
        private MethodDeclarationSyntax? CreateMethod(WaylandProtocol protocol, WaylandProtocolInterface @interface, WaylandProtocolMessage request, int index)
        {
            // TODO: Handle single and multiple Dispose (probably in runtime)
            
            var newIdArgument = request.Arguments?.FirstOrDefault(static a => a.Type == WaylandArgumentTypes.NewId);
            if (newIdArgument is not null && newIdArgument.Interface is null)
                return null;
            var ctorType = newIdArgument?.Interface;
            var dotNetCtorType = ctorType is null ? "void" : GetWlInterfaceTypeName(ctorType);

            var method = MethodDeclaration(ParseTypeName(dotNetCtorType), Pascalize(request.Name));
            var parameters = new List<ParameterSyntax>();

            var callVar = VariableDeclaration(ParseTypeName("var"), SingletonSeparatedList(
                VariableDeclarator("__call").WithInitializer(
                    EqualsValueClause(
                        InvokeMember(IdentifierName("global::NWayland.Interop.WaylandCallBuilder"), "Create",
                            IdentifierName("this"), MakeLiteralExpression(index))))));

            var callUsing = UsingStatement(callVar, null, Block());
            var callId = IdentifierName("__call");
            
            var body = new List<StatementSyntax>();
            
            foreach (var arg in request.Arguments ?? [])
            {
                var argName = $"@{Pascalize(arg.Name, true)}";
                TypeSyntax? castTo = null;
                if (arg.Type != WaylandArgumentTypes.NewId)
                {
                    var parameterTypeName = arg.Type switch
                    {
                        WaylandArgumentTypes.Int32 => "int",
                        WaylandArgumentTypes.FileDescriptor => "int",
                        WaylandArgumentTypes.UInt32 => "uint",
                        WaylandArgumentTypes.Fixed => "WlFixed",
                        WaylandArgumentTypes.String => "string",
                        WaylandArgumentTypes.Array =>
                            "System.ReadOnlySpan<" +
                            GetTypeNameForArray(protocol.Name, @interface.Name, request.Name, arg.Name) + ">",
                        WaylandArgumentTypes.Object => GetWlInterfaceTypeName(arg.Interface!),
                        _ => throw CreateError("Unknown type name: " + arg.Type)
                    };
                    
                    if (arg.Type is WaylandArgumentTypes.Int32 or WaylandArgumentTypes.UInt32)
                    {
                        var enumType = TryGetEnumTypeReference(protocol.Name, @interface.Name, request.Name,
                            arg.Name,
                            arg.Enum);
                        if (enumType != null)
                        {
                            castTo = ParseTypeName(parameterTypeName);
                            parameterTypeName = enumType;
                        }
                    }

                    parameters.Add(Parameter(Identifier(argName)).WithType(ParseTypeName(parameterTypeName)));
                }

                if (arg.Type == WaylandArgumentTypes.NewId)
                    body.Add(ExpressionStatement(InvokeMember(callId, "ArgNewId")));
                else
                {
                    var value = (ExpressionSyntax)IdentifierName(argName);
                    if (castTo != null)
                        value = CastExpression(castTo, value);
                    body.Add(ExpressionStatement(InvokeMember(callId, "Arg", value)));
                }
            }

            bool hasListener = false;
            if (newIdArgument != null)
            {
                if (@interface.Events != null && @interface.Events.Length > 0)
                {
                    parameters.Add(Parameter(Identifier("eventsListener"))
                        .WithType(ParseTypeName(GetWlInterfaceTypeName(newIdArgument.Interface!) + ".Listener")));
                    hasListener = true;
                }

                parameters.Add(Parameter(Identifier("dispatchOnQueue"))
                    .WithType(ParseTypeName("global::NWayland.WlEventQueue?")));
            }

            if (newIdArgument != null)
                body.Add(ReturnStatement(
                    CastExpression(ParseTypeName(GetWlInterfaceTypeName(newIdArgument.Interface!)),
                        InvokeMember(callId, "InvokeNewId",
                            MemberAccess(IdentifierName(GetWlInterfaceTypeName(newIdArgument.Interface!)), "ProxyType"),
                            hasListener ? IdentifierName("eventsListener") : MakeNullLiteralExpression(),
                            IdentifierName("dispatchOnQueue")
                        ))));
            else
                body.Add(ExpressionStatement(InvokeMember(callId, "Invoke")));

            return method.WithParameterList(ParameterList(SeparatedList(parameters)))
                .WithBody(Block(
                    callUsing.WithStatement(Block(List(body)))

                ));
        }


        private ClassDeclarationSyntax WithRequests(ClassDeclarationSyntax cl, WaylandProtocol protocol, WaylandProtocolInterface @interface)
        {
            if (@interface.Requests is null)
                return cl;
            for (var idx = 0; idx < @interface.Requests.Length; idx++)
            {
                var method = CreateMethod(protocol, @interface, @interface.Requests[idx], idx);
                if (method is not null)
                    cl = cl.AddMembers(method);
            }

            return cl;
        }
    }
}
