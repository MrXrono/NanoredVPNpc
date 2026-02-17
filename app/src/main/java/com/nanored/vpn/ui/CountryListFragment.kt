package com.nanored.vpn.ui

import android.graphics.Color
import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import androidx.fragment.app.activityViewModels
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.nanored.vpn.databinding.FragmentCountryListBinding
import com.nanored.vpn.databinding.ItemCountryBinding
import com.nanored.vpn.util.CountryUtils
import com.nanored.vpn.viewmodel.MainViewModel

class CountryListFragment : BaseFragment<FragmentCountryListBinding>() {

    private val mainViewModel: MainViewModel by activityViewModels()
    private var adapter: CountryAdapter? = null

    companion object {
        fun newInstance() = CountryListFragment()
    }

    override fun inflateBinding(inflater: LayoutInflater, container: ViewGroup?) =
        FragmentCountryListBinding.inflate(inflater, container, false)

    override fun onViewCreated(view: View, savedInstanceState: Bundle?) {
        binding.recyclerViewCountries.layoutManager = LinearLayoutManager(requireContext())
        mainViewModel.buildCountryList()

        mainViewModel.countries.observe(viewLifecycleOwner) { countries ->
            if (mainViewModel.selectedCountryCode == null && countries.isNotEmpty()) {
                mainViewModel.selectedCountryCode = countries.first().code
            }
            adapter = CountryAdapter(
                countries,
                mainViewModel.selectedCountryCode,
                mainViewModel.countryPingResults.value ?: emptyMap()
            ) { code ->
                val previousCode = mainViewModel.selectedCountryCode
                mainViewModel.selectedCountryCode = code
                adapter?.updateSelection(code)
                // If VPN is running and country changed, switch to new country
                if (mainViewModel.isRunning.value == true && previousCode != code) {
                    (activity as? MainActivity)?.switchCountry(code)
                }
            }
            binding.recyclerViewCountries.adapter = adapter
        }

        mainViewModel.countryPingResults.observe(viewLifecycleOwner) { pings ->
            adapter?.updatePings(pings)
        }
    }

    override fun onResume() {
        super.onResume()
        mainViewModel.subscriptionIdChanged("")
        mainViewModel.buildCountryList()
    }

    private class CountryAdapter(
        private val countries: List<MainViewModel.CountryInfo>,
        private var selectedCode: String?,
        private var pingResults: Map<String, Long>,
        private val onClick: (String) -> Unit
    ) : RecyclerView.Adapter<CountryAdapter.ViewHolder>() {

        companion object {
            fun serverWord(n: Int): String {
                val mod100 = n % 100
                val mod10 = n % 10
                return when {
                    mod100 in 11..19 -> "серверов"
                    mod10 == 1 -> "сервер"
                    mod10 in 2..4 -> "сервера"
                    else -> "серверов"
                }
            }
        }

        fun updateSelection(code: String) {
            selectedCode = code
            notifyDataSetChanged()
        }

        fun updatePings(pings: Map<String, Long>) {
            pingResults = pings
            notifyDataSetChanged()
        }

        class ViewHolder(val binding: ItemCountryBinding) : RecyclerView.ViewHolder(binding.root)

        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): ViewHolder {
            val binding = ItemCountryBinding.inflate(LayoutInflater.from(parent.context), parent, false)
            return ViewHolder(binding)
        }

        override fun onBindViewHolder(holder: ViewHolder, position: Int) {
            val country = countries[position]
            holder.binding.tvCountryFlag.text = country.flag
            holder.binding.tvCountryName.text = country.name
            val n = country.serverGuids.size
            holder.binding.tvServerCount.text = "$n ${serverWord(n)}"

            val ping = pingResults[country.code]
            holder.binding.tvCountryPing.text = if (ping != null && ping > 0) "${ping} мс" else ""

            val isSelected = country.code == selectedCode
            holder.binding.layoutCountry.setBackgroundColor(
                if (isSelected) Color.parseColor("#1A4CAF50") else Color.TRANSPARENT
            )

            holder.binding.root.setOnClickListener {
                selectedCode = country.code
                onClick(country.code)
            }
        }

        override fun getItemCount() = countries.size
    }
}
