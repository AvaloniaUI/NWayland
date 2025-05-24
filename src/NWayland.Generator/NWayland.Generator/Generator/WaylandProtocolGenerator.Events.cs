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
        private ClassDeclarationSyntax GenerateDelegatingClass(ClassDeclarationSyntax baseClass, string name, string fullBaseClassName)
        {
            var cl = ClassDeclaration(name)
                .AddBaseListTypes(SimpleBaseType(ParseTypeName(fullBaseClassName)))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.SealedKeyword)));

            foreach (var m in baseClass.Members.OfType<MethodDeclarationSyntax>())
            {
                var methodName = m.Identifier.ToString();
                if (methodName.Contains("."))
                    continue;
                
                var delegateName = methodName + "Delegate";
                var propName = "On" + methodName;
                cl = cl.AddMembers(DelegateDeclaration(ParseTypeName("void"), delegateName)
                    .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
                    .WithParameterList(m.ParameterList));

                cl = cl.AddMembers(PropertyDeclaration(ParseTypeName(delegateName + "?"), propName)
                        .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
                        .WithAccessorList(AccessorList(List([
                            AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                                .WithSemicolonToken(Semicolon()),
                            AccessorDeclaration(SyntaxKind.InitAccessorDeclaration)
                                .WithSemicolonToken(Semicolon())
                        ]))))
                    .WithLeadingTrivia(m.GetLeadingTrivia());
                cl = cl.AddMembers(MethodDeclaration(m.ReturnType, m.Identifier)
                    .WithModifiers(TokenList(Token(SyntaxKind.ProtectedKeyword), Token(SyntaxKind.OverrideKeyword)))
                    .WithParameterList(m.ParameterList)
                    .WithExpressionBody(ArrowExpressionClause(
                        ConditionalAccessExpression(IdentifierName(propName), 
                            InvocationExpression(MemberBindingExpression(IdentifierName("Invoke")),
                                ArgumentList(SeparatedList(
                                    m.ParameterList.Parameters.Select(p=>Argument(IdentifierName(p.Identifier)))))
                                
                                )))
                        ).WithSemicolonToken(Semicolon()
                    ));
            }

            return cl;

        }
        
        private ClassDeclarationSyntax WithEvents(ClassDeclarationSyntax cl, WaylandProtocol protocol,
            WaylandProtocolInterface @interface)
        {
            var evs = @interface.Events ?? Array.Empty<WaylandProtocolMessage>();

            var eventsClass = ClassDeclaration("Listener")
                .AddBaseListTypes(SimpleBaseType(ParseTypeName("global::NWayland.Interop.IWlEventsListener")))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.AbstractKeyword)));

            
            var switchStatement = SwitchStatement(MemberAccess(IdentifierName("arguments"), "Opcode"));
            var senderExpr = CastExpression(ParseTypeName(GetWlInterfaceTypeName(@interface.Name)),
                MemberAccess(IdentifierName("arguments"), "Sender"));

            for (var eventIndex = 0; eventIndex < evs.Length; eventIndex++)
            {
                var ev = evs[eventIndex];
                var eventName = $"{Pascalize(ev.Name)}";

                var handlerParameters = new SeparatedSyntaxList<ParameterSyntax>();

                handlerParameters = handlerParameters.Add(Parameter(Identifier("eventSender"))
                    .WithType(ParseTypeName(GetWlInterfaceTypeName(@interface.Name))));
                var arguments = new List<ExpressionSyntax>() { senderExpr.CrLfPrefix() };

                var eargs = ev.Arguments ?? Array.Empty<WaylandProtocolArgument>();
                for (var argIndex = 0; argIndex < eargs.Length; argIndex++)
                {
                    var arg = eargs[argIndex];
                    TypeSyntax? parameterType = null;

                    ExpressionSyntax bundle = IdentifierName("arguments");

                    var argName = $"@{Pascalize(arg.Name, true)}";
                    ExpressionSyntax argument;

                    var arrayTypeHint = GetTypeNameForArray(protocol.Name, @interface.Name, ev.Name, arg.Name);

                    string IfaceName() => GetWlInterfaceTypeName(arg.Interface
                                                                 ?? throw CreateError(
                                                                     "Don't know how to marshal events without interface",
                                                                     @interface, ev, arg));
                    var type = arg.Type switch
                    {
                        WaylandArgumentTypes.Int32 => "int",
                        WaylandArgumentTypes.UInt32 => "uint",
                        WaylandArgumentTypes.Fixed => "global::NWayland.WlFixed",
                        WaylandArgumentTypes.FileDescriptor => "int",
                        WaylandArgumentTypes.String => "string",
                        WaylandArgumentTypes.Object => GetWlInterfaceTypeNameOrWlProxy(arg.Interface),
                        WaylandArgumentTypes.Array =>
                            $"global::System.ReadOnlySpan<{arrayTypeHint}>",
                        WaylandArgumentTypes.NewId => $"global::NWayland.NewId<{IfaceName()}, {IfaceName()}.Listener>",
                        _ => throw CreateError("Unknown type: " + arg.Type)
                    };

                    var convertedType = type;
                    if (arg.Type == WaylandArgumentTypes.Int32 || arg.Type == WaylandArgumentTypes.UInt32)
                        convertedType =
                            TryGetEnumTypeReference(protocol.Name, @interface.Name, ev.Name, arg.Name, arg.Enum) ??
                            type;

                    parameterType = ParseTypeName(convertedType);

                    if (
                        arg.AllowNull && arg.Type is WaylandArgumentTypes.Object or WaylandArgumentTypes.String)
                        parameterType = NullableType(parameterType);

                    var argumentGetterName = arg.Type switch
                    {
                        WaylandArgumentTypes.Int32 => "GetInt32",
                        WaylandArgumentTypes.UInt32 => "GetUInt32",
                        WaylandArgumentTypes.FileDescriptor => "GetInt32",
                        WaylandArgumentTypes.Fixed => "GetWlFixed",
                        WaylandArgumentTypes.String => "GetString",
                        WaylandArgumentTypes.Object => $"GetProxy<{GetWlInterfaceTypeNameOrWlProxy(arg.Interface)}>",
                        WaylandArgumentTypes.Array => $"GetArray<{arrayTypeHint}>",
                        WaylandArgumentTypes.NewId => $"GetNewId<{IfaceName()}, {IfaceName()}.Listener>",

                    };

                    argument = InvokeMember(bundle, argumentGetterName, MakeLiteralExpression(argIndex))
                        .CrLfPrefix();

                    if (convertedType != type)
                        argument = CastExpression(ParseTypeName(convertedType), argument);

                    handlerParameters = handlerParameters.Add(Parameter(Identifier(argName)).WithType(parameterType));
                    arguments.Add(argument);
                }

                var method = MethodDeclaration(ParseTypeName("void"), eventName)
                    .WithModifiers(TokenList(Token(SyntaxKind.ProtectedKeyword), Token(SyntaxKind.VirtualKeyword)))
                    .WithParameterList(ParameterList(handlerParameters))
                    .WithBody(Block());
                
                method = WithSummary(method, ev.Description);

                eventsClass = eventsClass.AddMembers(method);

                switchStatement = switchStatement.AddSections(
                    SwitchSection(
                        SingletonList<SwitchLabelSyntax>(
                            CaseSwitchLabel(MakeLiteralExpression(eventIndex))),
                        List(new StatementSyntax[]
                            {
                                ExpressionStatement(
                                    InvokeMember(IdentifierName("this"), eventName, arguments.ToArray())),
                                BreakStatement()
                            }
                        )));
            }

            var dispatchEvent = MethodDeclaration(ParseTypeName("void"),
                    "global::NWayland.Interop.IWlEventsListener.DispatchEvent")
                .WithParameterList(ParameterList(SeparatedList(new[]
                    {
                        Parameter(Identifier("arguments"))
                            .WithType(ParseTypeName("global::NWayland.Interop.WlEventArgs"))
                    }
                ))).WithBody(switchStatement.Sections.Count != 0 ? Block(switchStatement) : Block());

            eventsClass = eventsClass.AddMembers(dispatchEvent);

            eventsClass =
                eventsClass.AddMembers(GenerateDelegatingClass(eventsClass, "Relay", "Listener"));
            
            cl = cl.AddMembers(eventsClass);
            
            return cl;
        }
    }
}
