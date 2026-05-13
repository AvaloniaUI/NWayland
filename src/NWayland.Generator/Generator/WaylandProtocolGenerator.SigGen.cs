using System;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using Parameter = System.Reflection.Metadata.Parameter;

namespace NWayland.Generator
{
    partial class WaylandProtocolGenerator
    {
        private ExpressionSyntax GenerateWlMessage(WaylandProtocolMessage msg)
        {
            var message =
                InvokeMemberCrLf(IdentifierName("global::NWayland.Interop.WlMessageDescription"),
                        "Create", MakeLiteralExpression(msg.Name))
                    .CrLfPrefix();
            
            if (msg.Since != 0)
                message = InvokeMemberCrLf(message, "SinceVersion", MakeLiteralExpression(msg.Since));

            if (msg.Type == "destructor")
                message = InvokeMemberCrLf(message, "IsDestructor");
            
            var descIdentifier = IdentifierName("global::NWayland.Interop.WlMessageArgumentDescription");
            
            foreach (var arg in msg.Arguments ?? Array.Empty<WaylandProtocolArgument>())
            {
                var type = arg.Type switch
                {
                    WaylandArgumentTypes.Int32 => nameof(WaylandArgumentTypes.Int32),
                    WaylandArgumentTypes.UInt32 => nameof(WaylandArgumentTypes.UInt32),
                    WaylandArgumentTypes.Fixed => nameof(WaylandArgumentTypes.Fixed),
                    WaylandArgumentTypes.String => nameof(WaylandArgumentTypes.String),
                    WaylandArgumentTypes.Object => nameof(WaylandArgumentTypes.Object),
                    WaylandArgumentTypes.NewId => nameof(WaylandArgumentTypes.NewId),
                    WaylandArgumentTypes.Array => nameof(WaylandArgumentTypes.Array),
                    WaylandArgumentTypes.FileDescriptor => nameof(WaylandArgumentTypes.FileDescriptor),
                    _ => throw CreateError("Unknown argument type " + arg.Type)
                };
                
                if (arg is { Type: WaylandArgumentTypes.NewId, Interface: null })
                {
                    message = InvokeMemberCrLf(message, "Add", MemberAccess(descIdentifier, "String"));
                    message = InvokeMemberCrLf(message, "Add", MemberAccess(descIdentifier, "UInt32"));
                    //u sun
                }

                ExpressionSyntax argDesc; 
                if (arg.Type is WaylandArgumentTypes.NewId or WaylandArgumentTypes.Object)
                {
                    ExpressionSyntax ifaceType = MakeNullLiteralExpression();
                    if (arg.Interface != null)
                    {
                        ifaceType = MemberAccess(IdentifierName(GetWlInterfaceTypeName(arg.Interface)), "ProxyType");
                    }
                    argDesc = InvokeMember(descIdentifier, type, ifaceType);
                }
                else
                {
                    argDesc = MemberAccess(descIdentifier, type);
                }

                if (arg.AllowNull)
                    argDesc = InvokeMember(argDesc, "AsNullable");

                message = InvokeMemberCrLf(message, "Add", argDesc);
            }

            return InvokeMemberCrLf(message, "Build");
            
        }
        
        private ExpressionSyntax GenerateWlMessageList(ExpressionSyntax builder, bool events, in WaylandProtocolMessage[]? messages)
        {
            if (messages == null)
                return builder;
            var name = events ? "AddEvent" : "AddMethod";
            foreach (var m in messages)
                builder = InvokeMemberCrLf(builder, name, GenerateWlMessage(m));

            return builder;
        }
        
        private ClassDeclarationSyntax WithSignature(ClassDeclarationSyntax cl, WaylandProtocolInterface iface)
        {
            var builder = (ExpressionSyntax)InvokeMemberCrLf(
                    IdentifierName("global::NWayland.Interop.WlInterfaceDescription"),
                    "Create", MakeLiteralExpression(iface.Name), MakeLiteralExpression(iface.Version))
                .CrLfPrefix();

            builder = GenerateWlMessageList(builder, false, iface.Requests);
            builder = GenerateWlMessageList(builder, true, iface.Events);

            var built = InvokeMemberCrLf(builder, "Build");
            
            var descriptorType = ParseTypeName("global::NWayland.Interop.WlProxyTypeDescriptor");
            var descriptor = ObjectCreationExpression(descriptorType,
                ArgumentList(SeparatedList(
                [
                    Argument(built),
                    Argument(TypeOfExpression(ParseTypeName(cl.Identifier.ToString()))),
                    Argument(ParenthesizedLambdaExpression(ParameterList(SeparatedList([
                            Parameter(Identifier("ctx")),
                        ])), null,
                        ObjectCreationExpression(ParseTypeName(cl.Identifier.ToString()), ArgumentList(SeparatedList([
                            Argument(IdentifierName("ctx")),
                        ])), null)
                    )),
                    Argument(MakeLiteralExpression(iface.Frozen))
                ])), null);

            cl = cl.AddMembers(PropertyDeclaration(descriptorType, "ProxyType")
                    .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.StaticKeyword)))
                    .WithAccessorList(AccessorList(List(new[]
                    {
                        AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                            .WithSemicolonToken(Semicolon()),
                    }))).WithInitializer(EqualsValueClause(descriptor)).WithSemicolonToken(Semicolon()));

            return WithBindMethod(cl, iface);
        }

        private ClassDeclarationSyntax WithBindMethod(ClassDeclarationSyntax cl, WaylandProtocolInterface iface)
        {
            if (iface.Name is "wl_display" or "wl_registry")
                return cl;
            var proxyTypeName = GetWlInterfaceTypeName(iface.Name);
            var proxyType = ParseTypeName(proxyTypeName);

            return cl.AddMembers(
                MethodDeclaration(proxyType, "Bind")
                    .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.StaticKeyword)))
                    .WithParameterList(ParameterList(SeparatedList(
                        [
                            Parameter(Identifier("registry"))
                                .WithType(ParseTypeName("global::NWayland.Protocols.Wayland.WlRegistry")),
                            Parameter(Identifier("name"))
                                .WithType(ParseTypeName("uint")),
                            Parameter(Identifier("version"))
                                .WithType(ParseTypeName("uint")),
                            Parameter(Identifier("eventsListener"))
                                .WithType(ParseTypeName(proxyTypeName + ".Listener?"))
                                .WithDefault(EqualsValueClause(MakeNullLiteralExpression())),
                            Parameter(Identifier("dispatchOnQueue"))
                                .WithType(ParseTypeName("global::NWayland.WlEventQueue?"))
                                .WithDefault(EqualsValueClause(MakeNullLiteralExpression()))

                        ]
                    )))
                    .WithExpressionBody(ArrowExpressionClause(
                            InvokeMember(IdentifierName("registry"), "Bind<" + proxyTypeName + ">",
                                IdentifierName("name"),
                                IdentifierName("ProxyType"),
                                IdentifierName("version"),
                                IdentifierName("eventsListener"),
                                IdentifierName("dispatchOnQueue")
                                
                                ).CrLfPrefix())).WithSemicolonToken(Semicolon()).CrLfPrefix()

                    );
        }
    }
}
