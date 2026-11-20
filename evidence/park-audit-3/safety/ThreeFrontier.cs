internal static class AllCases
{
    private static readonly float[] Values = {-1f, 0f, 1f, BitConverter.UInt32BitsToSingle(0x7fc04567)};
    internal static IEnumerable<(string Name, Inputs Input)> Create()
    {
        for (int shape=0;shape<6;shape++)
        {
            // Exactly 17 active providers trigger the large queue while 14 lower
            // ranked, initially live sentinels leave the first three competing.
            for(int pattern=0;pattern<4096;pattern++)
            {
                const int count=17;
                var providers=new Provider[count];
                var elements=new Element[9+count-3];
                var targets=new float[8];
                int symbols=pattern, at=0;
                for(int p=0;p<count;p++)
                {
                    int len=p<3?3:1;
                    float budget=shape>=3 && p==shape-3 ? .2f:100f;
                    providers[p]=new Provider{Entity=p,Start=at,Count=len,Total=budget,Remaining=budget};
                    for(int j=0;j<len;j++)
                    {
                        float key;
                        if(p>=3) key=-2f;
                        else if(j==0) key=(shape%3) switch {0=>1f,1=>2-p,_=>p==2?0f:1f};
                        else {key=Values[symbols&3];symbols>>=2;}
                        elements[at]=new Element{Target=(p+j)%4,Priority=key,X=(at+1)*.13f,Y=1f/((at%5)+1),Density=.75f,Length=1f};
                        at++;
                    }
                }
                yield return ($"three-frontier-{shape}",new Inputs(providers,elements,targets));
            }
        }
    }
}
