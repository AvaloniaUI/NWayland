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

            return cl;
        }
    }
}
