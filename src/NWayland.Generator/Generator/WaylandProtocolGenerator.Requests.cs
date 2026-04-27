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

            var method = MethodDeclaration(ParseTypeName(dotNetCtorType), Pascalize(request.Name))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)));
            method = WithSummary(method, request.Description);
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

                    var parameterType = ParseTypeName(parameterTypeName);
                    
                    if (arg.Type is WaylandArgumentTypes.Object
                        || (arg.AllowNull && arg.Type is WaylandArgumentTypes.String))
                        parameterType = NullableType(parameterType);

                    parameters.Add(Parameter(Identifier(argName)).WithType(parameterType));
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

            if (newIdArgument != null)
            {
                parameters.Add(Parameter(Identifier("eventsListener"))
                    .WithType(ParseTypeName(GetWlInterfaceTypeName(newIdArgument.Interface!) + ".Listener?"))
                    .WithDefault(EqualsValueClause(MakeNullLiteralExpression()))
                );

                parameters.Add(Parameter(Identifier("dispatchOnQueue"))
                    .WithType(ParseTypeName("global::NWayland.WlEventQueue?"))
                    .WithDefault(EqualsValueClause(MakeNullLiteralExpression()))
                );
            }

            if (newIdArgument != null)
                body.Add(ReturnStatement(
                    CastExpression(ParseTypeName(GetWlInterfaceTypeName(newIdArgument.Interface!)),
                        InvokeMember(callId, "InvokeNewId",
                            MemberAccess(IdentifierName(GetWlInterfaceTypeName(newIdArgument.Interface!)), "ProxyType"),
                            IdentifierName("eventsListener"),
                            IdentifierName("dispatchOnQueue")
                        ))));
            else
                body.Add(ExpressionStatement(InvokeMember(callId, "Invoke")));

            return method.WithParameterList(ParameterList(SeparatedList(parameters)))
                .WithBody(Block(
                    callUsing.WithStatement(Block(List(body)))

                ));
        }

        private PropertyDeclarationSyntax? GenerateAvailabilityCheck( WaylandProtocolMessage interfaceRequest, bool isEvent = false)
        {
            if (interfaceRequest.Since == 0)
                return null;
            var propertyName = $"Is{Pascalize(interfaceRequest.Name)}{(isEvent ? "Event" : "")}Available";
            return PropertyDeclaration(ParseTypeName("bool"), propertyName)
                .WithExpressionBody(
                    ArrowExpressionClause(
                        BinaryExpression(SyntaxKind.GreaterThanOrEqualExpression,
                            IdentifierName("Version"),
                            MakeLiteralExpression(interfaceRequest.Since)
                        )
                    ))
                .WithSemicolonToken(Semicolon())
                .AddModifiers(Token(SyntaxKind.PublicKeyword));
        }


        private ClassDeclarationSyntax WithRequests(ClassDeclarationSyntax cl, WaylandProtocol protocol, WaylandProtocolInterface @interface)
        {
            if (@interface.Requests != null)
                for (var idx = 0; idx < @interface.Requests.Length; idx++)
                {
                    var method = CreateMethod(protocol, @interface, @interface.Requests[idx], idx);
                    if (method is not null)
                        cl = cl.AddMembers(method);
                    var check = GenerateAvailabilityCheck(@interface.Requests[idx]);
                    if (check != null)
                        cl = cl.AddMembers(check);
                }

            if (@interface.Events != null)
            {
                foreach (var ev in @interface.Events)
                {
                    var check = GenerateAvailabilityCheck(ev, true);
                    if (check != null)
                        cl = cl.AddMembers(check);
                }
            }

            return cl;
        }

    }
}
