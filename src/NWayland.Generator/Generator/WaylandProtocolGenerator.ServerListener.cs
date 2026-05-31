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
        private ClassDeclarationSyntax GenerateServerListenerClass(WaylandProtocol protocol,
            WaylandProtocolInterface @interface)
        {
            var requests = @interface.Requests ?? Array.Empty<WaylandProtocolMessage>();

            var listenerClass = ClassDeclaration("ServerListener")
                .AddBaseListTypes(SimpleBaseType(ParseTypeName("global::NWayland.Interop.IWlEventsListener")))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.AbstractKeyword)));

            var switchStatement = SwitchStatement(MemberAccess(IdentifierName("arguments"), "Opcode"));

            for (var requestIndex = 0; requestIndex < requests.Length; requestIndex++)
            {
                var request = requests[requestIndex];
                var methodName = Pascalize(request.Name);

                var handlerParameters = new SeparatedSyntaxList<ParameterSyntax>();
                handlerParameters = handlerParameters.Add(Parameter(Identifier("resource"))
                    .WithType(ParseTypeName(GetWlInterfaceTypeName(@interface.Name) + ".Server")));

                var arguments = new List<ExpressionSyntax>
                {
                    CastExpression(
                        ParseTypeName(GetWlInterfaceTypeName(@interface.Name) + ".Server"),
                        MemberAccess(IdentifierName("arguments"), "Sender")).CrLfPrefix()
                };

                var rargs = request.Arguments ?? Array.Empty<WaylandProtocolArgument>();
                for (var argIndex = 0; argIndex < rargs.Length; argIndex++)
                {
                    var arg = rargs[argIndex];
                    var argName = $"@{Pascalize(arg.Name, true)}";

                    var arrayTypeHint = GetTypeNameForArray(protocol.Name, @interface.Name, request.Name, arg.Name);

                    var type = arg.Type switch
                    {
                        WaylandArgumentTypes.Int32 => "int",
                        WaylandArgumentTypes.UInt32 => "uint",
                        WaylandArgumentTypes.Fixed => "global::NWayland.WlFixed",
                        WaylandArgumentTypes.FileDescriptor => "global::NWayland.WaylandFd",
                        WaylandArgumentTypes.String => "string",
                        WaylandArgumentTypes.Object => arg.Interface != null
                            ? GetWlInterfaceTypeName(arg.Interface) + ".Server"
                            : "global::NWayland.Server.WlResource",
                        WaylandArgumentTypes.Array => $"global::System.ReadOnlySpan<{arrayTypeHint}>",
                        WaylandArgumentTypes.NewId => arg.Interface != null
                            ? $"global::NWayland.NewId<{GetWlInterfaceTypeName(arg.Interface)}.Server, {GetWlInterfaceTypeName(arg.Interface)}.ServerListener>"
                            : "global::NWayland.Interop.WlUntypedNewId",
                        _ => throw CreateError("Unknown type: " + arg.Type)
                    };

                    var convertedType = type;
                    if (arg.Type is WaylandArgumentTypes.Int32 or WaylandArgumentTypes.UInt32)
                        convertedType =
                            TryGetEnumTypeReference(protocol.Name, @interface.Name, request.Name, arg.Name, arg.Enum) ??
                            type;

                    TypeSyntax parameterType = ParseTypeName(convertedType);

                    if (arg.Type is WaylandArgumentTypes.Object
                        || (arg.AllowNull && arg.Type is WaylandArgumentTypes.String))
                        parameterType = NullableType(parameterType);

                    var argumentGetterName = arg.Type switch
                    {
                        WaylandArgumentTypes.Int32 => "GetInt32",
                        WaylandArgumentTypes.UInt32 => "GetUInt32",
                        WaylandArgumentTypes.FileDescriptor => "GetFd",
                        WaylandArgumentTypes.Fixed => "GetWlFixed",
                        WaylandArgumentTypes.String => "GetString",
                        WaylandArgumentTypes.Object => arg.Interface != null
                            ? $"GetServerResource<{GetWlInterfaceTypeName(arg.Interface)}.Server>"
                            : "GetServerResource<global::NWayland.Server.WlResource>",
                        WaylandArgumentTypes.Array => $"GetArray<{arrayTypeHint}>",
                        WaylandArgumentTypes.NewId => arg.Interface != null
                            ? $"GetServerNewId<{GetWlInterfaceTypeName(arg.Interface)}.Server, {GetWlInterfaceTypeName(arg.Interface)}.ServerListener>"
                            : "GetUntypedNewId",
                        _ => throw CreateError("Unknown request argument type: " + arg.Type),
                    };

                    ExpressionSyntax argument = InvokeMember(IdentifierName("arguments"), argumentGetterName,
                        MakeLiteralExpression(argIndex)).CrLfPrefix();

                    // GetString returns string?; a non-allow-null string arg has a non-nullable
                    // handler parameter, so suppress the nullable mismatch on the dispatch call.
                    if (arg.Type == WaylandArgumentTypes.String && !arg.AllowNull)
                        argument = PostfixUnaryExpression(SyntaxKind.SuppressNullableWarningExpression, argument);

                    if (convertedType != type)
                        argument = CastExpression(ParseTypeName(convertedType), argument);

                    handlerParameters = handlerParameters.Add(Parameter(Identifier(argName)).WithType(parameterType));
                    arguments.Add(argument);
                }

                var method = MethodDeclaration(ParseTypeName("void"), methodName)
                    .WithModifiers(TokenList(Token(SyntaxKind.ProtectedKeyword), Token(SyntaxKind.AbstractKeyword)))
                    .WithParameterList(ParameterList(handlerParameters))
                    .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));

                method = WithSummary(method, request.Description);
                listenerClass = listenerClass.AddMembers(method);

                switchStatement = switchStatement.AddSections(
                    SwitchSection(
                        SingletonList<SwitchLabelSyntax>(
                            CaseSwitchLabel(MakeLiteralExpression(requestIndex))),
                        List(new StatementSyntax[]
                        {
                            ExpressionStatement(
                                InvokeMember(IdentifierName("this"), methodName, arguments.ToArray())),
                            BreakStatement()
                        })));
            }

            var dispatchMethod = MethodDeclaration(ParseTypeName("void"),
                    "global::NWayland.Interop.IWlEventsListener.DispatchEvent")
                .WithParameterList(ParameterList(SeparatedList(new[]
                {
                    Parameter(Identifier("arguments"))
                        .WithType(ParseTypeName("global::NWayland.Interop.WlEventArgs"))
                })))
                .WithBody(switchStatement.Sections.Count != 0 ? Block(switchStatement) : Block());

            listenerClass = listenerClass.AddMembers(dispatchMethod);

            return listenerClass;
        }
    }
}
