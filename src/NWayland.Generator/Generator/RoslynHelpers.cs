using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
namespace NWayland.Generator;

static class RoslynHelpers
{
    public static TSyntax CrLf<TSyntax>(
        this TSyntax node)
        where TSyntax : SyntaxNode
    {
        return node.WithTrailingTrivia<TSyntax>(new SyntaxTrivia[] { Comment("//CRLF"), EndOfLine("\r\n") });
    }
    
    
    public static TSyntax CrLfPrefix<TSyntax>(
        this TSyntax node)
        where TSyntax : SyntaxNode
    {
        return node.WithLeadingTrivia<TSyntax>(new SyntaxTrivia[] { Comment("//CRLF"), EndOfLine("\r\n") });
    }
}