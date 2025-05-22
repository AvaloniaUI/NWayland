using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace NWayland.Generator
{
    partial class WaylandProtocolGenerator
    {
        private ClassDeclarationSyntax WithEvents(ClassDeclarationSyntax cl, WaylandProtocol protocol, WaylandProtocolInterface @interface)
        {
            var evs = @interface.Events ?? Array.Empty<WaylandProtocolMessage>();
            var eventInterface = InterfaceDeclaration("IEvents")
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)));

            var switchStatement= SwitchStatement(IdentifierName("opcode"));
            for (var eventIndex = 0; eventIndex < evs.Length; eventIndex++)
            {
                var ev = evs[eventIndex];
                var eventName = $"On{Pascalize(ev.Name)}";

                var handlerParameters = new SeparatedSyntaxList<ParameterSyntax>();
                var arguments = new SeparatedSyntaxList<ArgumentSyntax>();
                handlerParameters = handlerParameters.Add(Parameter(Identifier("eventSender"))
                    .WithType(ParseTypeName(GetWlInterfaceTypeName(@interface.Name))));
                arguments = arguments.Add(Argument(ThisExpression()));

                var eargs = ev.Arguments ?? Array.Empty<WaylandProtocolArgument>();
                for (var argIndex = 0; argIndex < eargs.Length; argIndex++)
                {
                    var arg = eargs[argIndex];
                    TypeSyntax? parameterType = null;

                    ExpressionSyntax argument = ElementAccessExpression(IdentifierName("arguments"),
                        BracketedArgumentList(SingletonSeparatedList(Argument(MakeLiteralExpression(argIndex)))));

                    var argName = $"@{Pascalize(arg.Name, true)}";
                    switch (arg.Type)
                    {
                        case WaylandArgumentTypes.Int32:
                        case WaylandArgumentTypes.Fixed:
                        case WaylandArgumentTypes.FileDescriptor:
                        case WaylandArgumentTypes.Uint32:
                        {
                            var nativeType = arg.Type switch
                            {
                                WaylandArgumentTypes.Int32 => "int",
                                WaylandArgumentTypes.Uint32 => "uint",
                                WaylandArgumentTypes.Fixed => "WlFixed",
                                _ => "int"
                            };

                            var managedType =
                                TryGetEnumTypeReference(protocol.Name, @interface.Name, ev.Name, arg.Name, arg.Enum) ??
                                nativeType;

                            parameterType = ParseTypeName(managedType);
                            var fieldName = arg.Type switch
                            {
                                WaylandArgumentTypes.Int32 => "Int32",
                                WaylandArgumentTypes.Uint32 => "UInt32",
                                WaylandArgumentTypes.Fixed => "WlFixed",
                                _ => "Int32"
                            };

                            argument = MemberAccess(argument, fieldName);

                            if (managedType != nativeType)
                                argument = CastExpression(ParseTypeName(managedType), argument);
                            break;
                        }
                        case WaylandArgumentTypes.NewId:
                            parameterType = ParseTypeName(Pascalize(arg.Interface));
                            argument = ObjectCreationExpression(parameterType)
                                .WithArgumentList(
                                    ArgumentList(SeparatedList(new[]
                                        {
                                            Argument(MemberAccess(argument, "IntPtr")),
                                            Argument(IdentifierName("Version"))
                                        }
                                    )));
                            break;
                        case WaylandArgumentTypes.String:
                            parameterType = arg.AllowNull ? NullableType(ParseTypeName("string")) : ParseTypeName("string");

                            argument = InvocationExpression(
                                MemberAccess(ParseTypeName("Marshal"), "PtrToStringAnsi"),
                                ArgumentList(SingletonSeparatedList(Argument(MemberAccess(argument, "IntPtr")))));
                            break;
                        case WaylandArgumentTypes.Object:
                        {
                            var parameterTypeString = arg.Interface is null ? "WlProxy" : GetWlInterfaceTypeName(arg.Interface);
                            parameterType = arg.AllowNull ? NullableType(ParseTypeName(parameterTypeString)) : ParseTypeName(parameterTypeString);
                            argument = InvocationExpression(MemberAccess(ParseTypeName("WlProxy"),
                                    $"FromNative<{parameterTypeString}>"),
                                ArgumentList(SingletonSeparatedList(Argument(MemberAccess(argument, "IntPtr")))));
                            break;
                        }
                        case WaylandArgumentTypes.Array when arg.AllowNull:
                            throw new NotSupportedException("Wrapping nullable arrays is currently not supported");
                        case WaylandArgumentTypes.Array:
                        {
                            var arrayElementType = GetTypeNameForArray(protocol.Name, @interface.Name, ev.Name, arg.Name);
                            parameterType = ParseTypeName($"ReadOnlySpan<{arrayElementType}>");
                            argument = InvocationExpression(
                                MemberAccess(ParseTypeName("WlArray"), $"SpanFromWlArrayPtr<{arrayElementType}>"),
                                ArgumentList(SingletonSeparatedList(Argument(MemberAccess(argument, "IntPtr")))));
                            break;
                        }
                    }

                    handlerParameters = handlerParameters.Add(Parameter(Identifier(argName)).WithType(parameterType));
                    arguments = arguments.Add(Argument(argument));
                }

                var method = MethodDeclaration(ParseTypeName("void"), eventName)
                    .WithParameterList(ParameterList(handlerParameters))
                    .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));

                method = WithSummary(method, ev.Description);

                eventInterface = eventInterface.AddMembers(method);

                switchStatement = switchStatement.AddSections(
                    SwitchSection(
                        SingletonList<SwitchLabelSyntax>(
                            CaseSwitchLabel(MakeLiteralExpression(eventIndex))),
                        List(new StatementSyntax[]
                        {
                            ExpressionStatement(ConditionalAccessExpression(IdentifierName("Events"),
                                InvocationExpression(MemberBindingExpression(IdentifierName(eventName)))
                                    .WithArgumentList(ArgumentList(arguments)))),
                            BreakStatement()
                        }
                        )));
            }

            cl = cl.AddMembers(eventInterface);
            cl = cl.AddMembers(PropertyDeclaration(NullableType(ParseTypeName("IEvents")), "Events")
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
                .WithAccessorList(AccessorList(List(new[]
                {
                    AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                        .WithSemicolonToken(Semicolon()),
                    AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                        .WithSemicolonToken(Semicolon())
                })))
            );

            var dispatchEvent = MethodDeclaration(ParseTypeName("void"), "DispatchEvent")
                .WithModifiers(TokenList(Token(SyntaxKind.ProtectedKeyword), Token(SyntaxKind.OverrideKeyword)))
                .WithParameterList(ParameterList(SeparatedList(new[]
                    {
                        Parameter(Identifier("opcode")).WithType(ParseTypeName("uint")),
                        Parameter(Identifier("arguments")).WithType(ParseTypeName("WlArgument*"))
                    }
                )));

            cl = cl.AddMembers(
                dispatchEvent.WithBody(switchStatement.Sections.Count != 0 ? Block(switchStatement) : Block()));

            return cl;
        }
    }
}
