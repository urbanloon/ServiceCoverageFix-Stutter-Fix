internal static class AllCases
{
    internal static IEnumerable<(string Name, Inputs Input)> Create()
    {
        foreach(bool hotShared in new[]{false,true})
        foreach(bool tailsShared in new[]{false,true})
        {
            const int n=384, length=704;
            var providers=new Provider[n];
            var elements=new Element[n*length];
            var targets=new float[2*2000];
            for(int p=0;p<n;p++)
            {
                providers[p]=new Provider{Entity=p,Start=p*length,Count=length,Total=.1f,Remaining=.1f};
                for(int j=0;j<length;j++)
                    elements[p*length+j]=new Element{Target=j==0 ? (hotShared?0:p) : (tailsShared?1000+p%2:1100+p),X=p+1,Y=p+1,Priority=n-p-j,Density=1f,Length=1000000f};
            }
            yield return ($"hot-{hotShared}-tails-{tailsShared}",new Inputs(providers,elements,targets));
        }
    }
}
