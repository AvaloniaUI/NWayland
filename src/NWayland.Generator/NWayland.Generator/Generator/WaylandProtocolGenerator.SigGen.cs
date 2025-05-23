using System;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace NWayland.Generator
{
    partial class WaylandProtocolGenerator
    {
        private ObjectCreationExpressionSyntax GenerateWlMessage(WaylandProtocolMessage msg)
        {
            var signature = new StringBuilder();
            if (msg.Since != 0)
                signature.Append(msg.Since);
            var interfaceList = new SeparatedSyntaxList<ExpressionSyntax>();
            if (msg.Arguments is not null)
                foreach (var arg in msg.Arguments)
                {
                    if (arg.AllowNull)
                        signature.Append('?');
                    if (arg.Type == WaylandArgumentTypes.NewId && arg.Interface is null)
                    {
                        signature.Append("su");
                        interfaceList = interfaceList.AddRange(new[] { MakeNullLiteralExpression(), MakeNullLiteralExpression() });
                    }

                    signature.Append(WaylandArgumentTypes.NamesToCodes[arg.Type]);
                    if (arg.Interface is not null)
                        interfaceList = interfaceList.Add(
                            GetWlInterfaceAddressFor(arg.Interface));
                    else
                        interfaceList = interfaceList.Add(MakeNullLiteralExpression());
                }

            var argList = ArgumentList(SeparatedList(new[]
            {
                Argument(MakeLiteralExpression(msg.Name)),
                Argument(MakeLiteralExpression(signature.ToString())),
                Argument(ArrayCreationExpression(ArrayType(ParseTypeName("WlInterface*[]")))
                    .WithInitializer(InitializerExpression(SyntaxKind.ArrayInitializerExpression,
                        interfaceList)))

            }));

            return ObjectCreationExpression(ParseTypeName("WlMessage"), argList, null)
                .WithLeadingTrivia(CarriageReturn);
        }

        private ArgumentSyntax GenerateWlMessageList(in WaylandProtocolMessage[] messages)
        {
            var elements = new SeparatedSyntaxList<ExpressionSyntax>();
            foreach (var msg in messages)
                elements = elements.Add(GenerateWlMessage(msg));
            return Argument(ArrayCreationExpression(ArrayType(ParseTypeName("WlMessage[]")), InitializerExpression(
                SyntaxKind.ArrayInitializerExpression,
                elements)));
        }

        private ClassDeclarationSyntax WithSignature(ClassDeclarationSyntax cl, WaylandProtocolInterface @interface)
        {
            var attr = AttributeList(SingletonSeparatedList(
                Attribute(
                    IdentifierName("FixedAddressValueType"))
            )).NormalizeWhitespace();
            var sigField = FieldDeclaration(new SyntaxList<AttributeListSyntax>(
                    new[] {attr}),
                new SyntaxTokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.StaticKeyword)),
                VariableDeclaration(ParseTypeName("WlInterface"))
                    .AddVariables(VariableDeclarator("WlInterface")));
            cl = cl.AddMembers(sigField);

            var staticCtor = ConstructorDeclaration(cl.Identifier)
                .AddModifiers(Token(SyntaxKind.StaticKeyword));

            var args = ArgumentList(SeparatedList(new[]
                {
                    Argument(MakeLiteralExpression(@interface.Name)),
                    Argument(MakeLiteralExpression(@interface.Version)),
                    GenerateWlMessageList(@interface.Requests?.Cast<WaylandProtocolMessage>().ToArray() ?? Array.Empty<WaylandProtocolMessage>()),
                    GenerateWlMessageList(@interface.Events ?? Array.Empty<WaylandProtocolMessage>())
                }
            ));

            staticCtor = staticCtor.AddBodyStatements(
                ExpressionStatement(AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName(GetWlInterfaceTypeName(@interface.Name)), IdentifierName("WlInterface")),
                    ObjectCreationExpression(ParseTypeName("WlInterface"), args, null)
                ))
            );

            cl = cl.AddMembers(staticCtor);

            cl = cl.AddMembers(MethodDeclaration(ParseTypeName("WlInterface*"), "GetWlInterface")
                .WithModifiers(TokenList(Token(SyntaxKind.ProtectedKeyword), Token(SyntaxKind.OverrideKeyword)))
                .WithBody(Block(ReturnStatement(GetWlInterfaceAddressFor(@interface.Name)))));
            cl = WithSignature2(cl, @interface, staticCtor);
            return cl;
        }
        
        
        private ExpressionSyntax GenerateWlMessage2(WaylandProtocolMessage msg)
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
        
        private ExpressionSyntax GenerateWlMessageList2(ExpressionSyntax builder, bool events, in WaylandProtocolMessage[]? messages)
        {
            if (messages == null)
                return builder;
            var name = events ? "AddEvent" : "AddMethod";
            foreach (var m in messages)
                builder = InvokeMemberCrLf(builder, name, GenerateWlMessage2(m));

            return builder;
        }
        
        
        private ClassDeclarationSyntax WithSignature2(ClassDeclarationSyntax cl, WaylandProtocolInterface iface, ConstructorDeclarationSyntax ctor)
        {
            var builder = (ExpressionSyntax)InvokeMemberCrLf(
                    IdentifierName("global::NWayland.Interop.WlInterfaceDescription"),
                    "Create", MakeLiteralExpression(iface.Name), MakeLiteralExpression(iface.Version))
                .CrLfPrefix();

            builder = GenerateWlMessageList2(builder, false, iface.Requests);
            builder = GenerateWlMessageList2(builder, true, iface.Events);

            var built = InvokeMemberCrLf(builder, "Build");


            var descriptorType = ParseTypeName("global::NWayland.Interop.WlProxyTypeDescriptor");
            var descriptor = ObjectCreationExpression(descriptorType,
                ArgumentList(SeparatedList(
                [
                    Argument(built),
                    Argument(TypeOfExpression(ParseTypeName(cl.Identifier.ToString()))),
                    Argument(ParenthesizedLambdaExpression(ParameterList(SeparatedList([
                            Parameter(Identifier("ctx")),
                            Parameter(Identifier("handle")),
                            Parameter(Identifier("iface")),
                            Parameter(Identifier("ownsHandle")),
                        ])), null,
                        ObjectCreationExpression(ParseTypeName(cl.Identifier.ToString()), ArgumentList(SeparatedList([
                            Argument(IdentifierName("ctx")),
                            Argument(IdentifierName("handle")),
                            Argument(IdentifierName("iface")),
                            Argument(IdentifierName("ownsHandle"))
                        ])), null)
                    ))
                ])), null);

            return cl.AddMembers(PropertyDeclaration(descriptorType, "ProxyType")
                    .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.StaticKeyword)))
                    .WithAccessorList(AccessorList(List(new[]
                    {
                        AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                            .WithSemicolonToken(Semicolon()),
                    }))).WithInitializer(EqualsValueClause(descriptor)).WithSemicolonToken(Semicolon()));

            //public record WlProxyTypeDescriptor(WlInterfaceDescription Interface, Type ProxyType, WlProxyFactory Factory);

            /*cl.AddMembers(PropertyDeclaration(NullableType(ParseTypeName("IEvents")), "Events")
               .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
               .WithAccessorList(AccessorList(List(new[]
               {
                   AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                       .WithSemicolonToken(Semicolon()),
                   AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                       .WithSemicolonToken(Semicolon())
               })))*/

            //cl.WithMembers(PropertyDeclaration())

            //cl.ReplaceNode(ctor, ctor.AddBodyStatements())
        }
    }
}
