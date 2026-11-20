// Reuse the repository's validated fixed-buffer record decoder.
#define main original_release_decoder_main
#include "read_release_trace.cpp"
#undef main
#include <set>

int main(int argc, char** argv) {
    if (argc!=4) return 2;
    Trace trace(argv[1]);
    std::ifstream threads(argv[2]);
    std::set<unsigned> game;
    unsigned tid;
    std::string role;
    while (threads >> tid >> role) game.insert(tid);
    if (game.empty()) return 2;
    std::map<unsigned,std::vector<Switch>> timeline;
    struct Edge { Tick t; unsigned cpu, source, target; };
    std::vector<Edge> ready;
    trace.scan([&](Bytes p,Bytes end,Bytes d,Tick t,unsigned cpu){
        if (!d || p[7]!=5) return;
        if (p[6]==36 && d+24<=end)
            timeline[cpu].push_back({t,read_value<unsigned>(d),read_value<unsigned>(d+4),d[12],d[14]});
        if (p[6]==50 && d+8<=end && game.count(read_value<unsigned>(d)))
            ready.push_back({t,cpu,p[2]==17?0:read_value<unsigned>(p+8),read_value<unsigned>(d)});
    });
    std::ofstream f(std::string(argv[3])+".dispatch");
    for(auto& [cpu,sw]:timeline) {
        std::sort(sw.begin(),sw.end(),[](auto& a,auto& b){return a.t<b.t;});
        for (auto& s:sw) {
            if(game.count(s.next)) f<<"I "<<s.t<<" "<<s.next<<" "<<cpu<<" 0\n";
            if(game.count(s.old)) f<<"O "<<s.t<<" "<<s.old<<" "<<cpu<<" "<<int(s.state)<<"\n";
        }
    }
    for(auto& e:ready) {
        auto &sw=timeline.at(e.cpu);
        auto it=std::upper_bound(sw.begin(),sw.end(),e.t,[](Tick t,const Switch& s){return t<s.t;});
        unsigned source=0;
        if(it!=sw.begin()) {
            --it;
            if(!e.source || e.source==it->next) source=it->next;
        }
        f<<"R "<<e.t<<" "<<e.target<<" "<<e.cpu<<" "<<source<<"\n";
    }
    return f ? 0 : 2;
}
