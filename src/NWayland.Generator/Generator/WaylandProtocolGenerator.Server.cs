using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace NWayland.Generator
{
    partial class WaylandProtocolGenerator
    {
        private ClassDeclarationSyntax GenerateServerClass(WaylandProtocol protocol,
            WaylandProtocolInterface @interface)
        {
            var serverClass = ClassDeclaration("Server")
                .AddBaseListTypes(SimpleBaseType(ParseTypeName("global::NWayland.Server.WlResource")),
                    SimpleBaseType(ParseTypeName("global::NWayland.Interop.IWlProxyTypeDescriptorProvider")))
                .WithModifiers(TokenList(
                    Token(SyntaxKind.PublicKeyword),
                    Token(SyntaxKind.SealedKeyword),
                    Token(SyntaxKind.UnsafeKeyword),
                    Token(SyntaxKind.PartialKeyword)));
            serverClass = serverClass.AddMembers(
                PropertyDeclaration(ParseTypeName("global::NWayland.Interop.WlProxyTypeDescriptor"), "ProxyType")
                    .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.StaticKeyword)))
                    .WithExpressionBody(
                        ArrowExpressionClause(MemberAccess(IdentifierName(GetWlInterfaceTypeName(@interface.Name)), "ProxyType")))
                    .WithSemicolonToken(Semicolon()));

            // Internal constructor (accessible by factory lambda in parent class)
            serverClass = serverClass.AddMembers(ConstructorDeclaration("Server")
                .AddModifiers(Token(SyntaxKind.InternalKeyword))
                .WithParameterList(ParameterList(SeparatedList(new[]
                {
                    Parameter(Identifier("context"))
                        .WithType(ParseTypeName("global::NWayland.Server.WlResourceCreationContext"))
                })))
                .WithBody(Block())
                .WithInitializer(ConstructorInitializer(SyntaxKind.BaseConstructorInitializer,
                    ArgumentList(SeparatedList(new[]
                    {
                        Argument(IdentifierName("context"))
                    })))));

            // Generate methods for events (server sends events to client)
            var events = @interface.Events ?? Array.Empty<WaylandProtocolMessage>();
            for (var eventIndex = 0; eventIndex < events.Length; eventIndex++)
            {
                var ev = events[eventIndex];
                var method = CreateServerEventMethod(protocol, @interface, ev, eventIndex);
                if (method != null)
                    serverClass = serverClass.AddMembers(method);
            }

            return serverClass;
        }

        private MethodDeclarationSyntax? CreateServerEventMethod(WaylandProtocol protocol,
            WaylandProtocolInterface @interface, WaylandProtocolMessage ev, int index)
        {
            var newIdArgument = ev.Arguments?.FirstOrDefault(static a => a.Type == WaylandArgumentTypes.NewId);
            if (newIdArgument is not null && newIdArgument.Interface is null)
                return null;

            var returnType = newIdArgument?.Interface is null
                ? "void"
                : GetWlInterfaceTypeName(newIdArgument.Interface) + ".Server";

            var method = MethodDeclaration(ParseTypeName(returnType), Pascalize(ev.Name))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)));
            method = WithSummary(method, ev.Description);

            var parameters = new List<ParameterSyntax>();

            var callVar = VariableDeclaration(ParseTypeName("var"), SingletonSeparatedList(
                VariableDeclarator("__call").WithInitializer(
                    EqualsValueClause(
                        InvokeMember(IdentifierName("global::NWayland.Interop.WaylandCallBuilder"), "Create",
                            IdentifierName("this"), MakeLiteralExpression(index))))));

            var callUsing = UsingStatement(callVar, null, Block());
            var callId = IdentifierName("__call");

            var body = new List<StatementSyntax>();

            foreach (var arg in ev.Arguments ?? Array.Empty<WaylandProtocolArgument>())
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
                            GetTypeNameForArray(protocol.Name, @interface.Name, ev.Name, arg.Name) + ">",
                        WaylandArgumentTypes.Object => arg.Interface != null
                            ? GetWlInterfaceTypeName(arg.Interface) + ".Server"
                            : "global::NWayland.Server.WlResource",
                        _ => throw CreateError("Unknown type name: " + arg.Type)
                    };

                    if (arg.Type is WaylandArgumentTypes.Int32 or WaylandArgumentTypes.UInt32)
                    {
                        var enumType = TryGetEnumTypeReference(protocol.Name, @interface.Name, ev.Name, arg.Name,
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
                parameters.Add(Parameter(Identifier("requestsListener"))
                    .WithType(ParseTypeName(GetWlInterfaceTypeName(newIdArgument.Interface!) + ".ServerListener?"))
                    .WithDefault(EqualsValueClause(MakeNullLiteralExpression()))
                );

                body.Add(ReturnStatement(
                    CastExpression(ParseTypeName(GetWlInterfaceTypeName(newIdArgument.Interface!) + ".Server"),
                        InvokeMember(callId, $"InvokeNewId<{GetWlInterfaceTypeName(newIdArgument.Interface!)}.Server>",
                            IdentifierName("requestsListener"),
                            MakeNullLiteralExpression()
                        ))));
            }
            else
                body.Add(ExpressionStatement(InvokeMember(callId, "Invoke")));

            return method.WithParameterList(ParameterList(SeparatedList(parameters)))
                .WithBody(Block(callUsing.WithStatement(Block(List(body)))));
        }

        private ClassDeclarationSyntax WithServer(ClassDeclarationSyntax cl, WaylandProtocol protocol,
            WaylandProtocolInterface @interface)
        {
            var serverClass = GenerateServerClass(protocol, @interface);
            var serverListenerClass = GenerateServerListenerClass(protocol, @interface);

            cl = cl.AddMembers(serverClass);
            cl = cl.AddMembers(serverListenerClass);
            return cl;
        }
    }
}
